using System.Text.Json;

namespace Penumbra.Core;

/// <summary>
/// A <see cref="IGlyphBank"/> backed by a single JSON file (grouped symbol → exemplars). The store path
/// is injected so tests can point it at a temp directory. Loads any existing store on construction and
/// tolerates a missing file (a fresh, empty bank). Each <see cref="Capture"/> rewrites the file via a
/// temp-file-then-move so a crash mid-write can't leave a half-written store.
/// </summary>
public sealed class JsonGlyphBank : IGlyphBank
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Sample-time outlier rejection. The bank file is also the owned raw-ink corpus (ADR-0006), so poison
    // already on disk (multi-symbol captures, mislabelled junk) is never deleted — it is filtered out of the
    // synthesis read path here. Constants: an exemplar's aspect must sit within a 1.6x factor of the symbol's
    // MEDIAN aspect and its stroke count must be <= median + 1. Below MinExemplars there isn't enough signal
    // to tell poison from a legit-but-unusual hand, so we don't filter at all.
    private const double AspectFactor = 1.6;
    private const double EpsilonFraction = 0.05;
    private const int MinExemplars = 3;

    private readonly string _storePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<GlyphSample>> _bySymbol;

    /// <summary>Opens (or creates) a bank persisted at <paramref name="storePath"/>.</summary>
    public JsonGlyphBank(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = storePath;
        _bySymbol = Load(storePath);
    }

    /// <inheritdoc />
    public void Capture(GlyphSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_gate)
        {
            if (!_bySymbol.TryGetValue(sample.Symbol, out List<GlyphSample>? samples))
            {
                samples = new List<GlyphSample>();
                _bySymbol[sample.Symbol] = samples;
            }

            // Dedup: pressing Recognize N times on one page re-recognizes the same document strokes and would
            // re-bank identical ink each time, skewing recency weighting toward whatever page is on screen.
            // Two exemplars built from the same strokes carry the same set of Stroke.Id guids; treat a match
            // as already-banked (no-op). So one page's glyph enters the corpus at most once, however many
            // times it is recognized.
            var incomingIds = new HashSet<Guid>(sample.Strokes.Select(s => s.Id));
            foreach (GlyphSample existing in samples)
            {
                if (incomingIds.SetEquals(existing.Strokes.Select(s => s.Id)))
                {
                    return;   // identical re-recognition — already in the corpus
                }
            }

            samples.Add(sample);
            Save();
        }
    }

    /// <inheritdoc />
    public bool Has(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        lock (_gate)
        {
            return _bySymbol.TryGetValue(symbol, out List<GlyphSample>? samples) && samples.Count > 0;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<GlyphSample> Samples(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        lock (_gate)
        {
            return _bySymbol.TryGetValue(symbol, out List<GlyphSample>? samples)
                ? samples.ToList()   // snapshot so callers can't mutate the bank's backing list
                : Array.Empty<GlyphSample>();
        }
    }

    /// <inheritdoc />
    public GlyphSample? Sample(string symbol, Random random)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(random);

        GlyphSample[] snapshot;
        lock (_gate)
        {
            if (!_bySymbol.TryGetValue(symbol, out List<GlyphSample>? samples) || samples.Count == 0)
            {
                return null;
            }

            // Snapshot under the gate, then run the weighted draw outside it: the caller-shared Random must
            // not be exercised while holding the lock, and the store must not mutate mid-draw.
            snapshot = samples.ToArray();
        }

        // Drop geometric outliers (poison) before weighting; order is preserved so recency still holds.
        snapshot = FilterOutliers(snapshot);

        // Linear recency weighting: exemplars are stored oldest-first, so index i (0-based) gets weight i+1.
        // The newest exemplar (weight N) is therefore N times as likely as the oldest (weight 1) while every
        // exemplar keeps a nonzero chance. Total weight = N(N+1)/2.
        int n = snapshot.Length;
        long totalWeight = (long)n * (n + 1) / 2;
        double target = random.NextDouble() * totalWeight;

        long cumulative = 0;
        for (int i = 0; i < n; i++)
        {
            cumulative += i + 1;
            if (target < cumulative)
            {
                return snapshot[i];
            }
        }

        return snapshot[n - 1]; // guard: floating-point could land target exactly on totalWeight
    }

    /// <summary>
    /// Returns the exemplars eligible for synthesis: those whose aspect is within <see cref="AspectFactor"/>x
    /// of the population median aspect AND whose stroke count is at most median + 1. Below
    /// <see cref="MinExemplars"/> the population is too small to judge, so all are kept. If the filter empties
    /// the pool (e.g. a bimodal population), we keep the single exemplar nearest the median aspect rather than
    /// return nothing — a slightly-off real glyph beats falling back to the cold-start font. Order is
    /// preserved (oldest-first) so downstream recency weighting is unaffected.
    /// </summary>
    private static GlyphSample[] FilterOutliers(GlyphSample[] snapshot)
    {
        if (snapshot.Length < MinExemplars)
        {
            return snapshot;
        }

        var aspects = new double[snapshot.Length];
        var strokeCounts = new double[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
        {
            aspects[i] = AspectRatio(snapshot[i]);
            strokeCounts[i] = snapshot[i].Strokes.Count;
        }

        double medianAspect = Median(aspects);
        double medianStrokes = Median(strokeCounts);

        var eligible = new List<GlyphSample>(snapshot.Length);
        for (int i = 0; i < snapshot.Length; i++)
        {
            bool aspectOk = aspects[i] >= medianAspect / AspectFactor && aspects[i] <= medianAspect * AspectFactor;
            bool strokesOk = strokeCounts[i] <= medianStrokes + 1;
            if (aspectOk && strokesOk)
            {
                eligible.Add(snapshot[i]);
            }
        }

        if (eligible.Count > 0)
        {
            return eligible.ToArray();
        }

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < snapshot.Length; i++)
        {
            double distance = Math.Abs(aspects[i] - medianAspect);
            if (distance < best)
            {
                best = distance;
                nearest = i;
            }
        }

        return new[] { snapshot[nearest] };
    }

    /// <summary>
    /// Width/height aspect of an exemplar's bounding box, with a per-exemplar epsilon floor on both axes
    /// (floor = <see cref="EpsilonFraction"/> * max(W, H, 1)). The floor is what keeps degenerate-but-legit
    /// ink comparable: a "-" (H ~= 0) or "1" bar or "." dot would otherwise produce an astronomical or
    /// zero-division aspect; flooring collapses each such class to a stable value (~20 for dashes, ~0.05 for
    /// bars, ~1 for dots) so they cluster instead of registering as outliers.
    /// </summary>
    private static double AspectRatio(GlyphSample sample)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (Stroke stroke in sample.Strokes)
        {
            foreach (StrokeSample p in stroke.Samples)
            {
                any = true;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        if (!any)
        {
            return 1.0;
        }

        double w = maxX - minX;
        double h = maxY - minY;
        double floor = EpsilonFraction * Math.Max(Math.Max(w, h), 1.0);
        return Math.Max(w, floor) / Math.Max(h, floor);
    }

    private static double Median(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(_bySymbol, Options);
        string tempPath = _storePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _storePath, overwrite: true);
    }

    private static Dictionary<string, List<GlyphSample>> Load(string storePath)
    {
        if (!File.Exists(storePath))
        {
            return new Dictionary<string, List<GlyphSample>>();
        }

        string json = File.ReadAllText(storePath);
        return JsonSerializer.Deserialize<Dictionary<string, List<GlyphSample>>>(json, Options)
            ?? new Dictionary<string, List<GlyphSample>>();
    }
}
