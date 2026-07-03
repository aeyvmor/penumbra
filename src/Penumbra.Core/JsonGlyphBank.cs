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
