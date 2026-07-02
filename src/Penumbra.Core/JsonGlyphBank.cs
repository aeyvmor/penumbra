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
