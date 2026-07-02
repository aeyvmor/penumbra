using Penumbra.Core;

namespace Penumbra.Core.Tests;

/// <summary>
/// 3.9d glyph bank: captures persist across sessions bit-exact, and a fresh bank tolerates a missing
/// store. Uses a temp directory so nothing touches the real per-user store.
/// </summary>
public sealed class JsonGlyphBankTests : IDisposable
{
    private readonly string _dir;
    private readonly string _storePath;

    public JsonGlyphBankTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"penumbra-glyphbank-{Guid.NewGuid():N}");
        _storePath = Path.Combine(_dir, "glyphbank.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void MissingFile_StartsEmpty()
    {
        var bank = new JsonGlyphBank(_storePath);

        Assert.False(bank.Has("3"));
        Assert.Empty(bank.Samples("3"));
    }

    [Fact]
    public void Capture_RoundTripsBitExactOnReload()
    {
        GlyphSample sample = Sample("3", deviceClass: "mouse");

        var bank = new JsonGlyphBank(_storePath);
        bank.Capture(sample);

        // A fresh instance on the same path must reload what the first one persisted.
        var reloaded = new JsonGlyphBank(_storePath);

        Assert.True(reloaded.Has("3"));
        GlyphSample loaded = Assert.Single(reloaded.Samples("3"));
        AssertSameGlyph(sample, loaded);
    }

    [Fact]
    public void Capture_AppendsMultipleSamplesPerSymbol()
    {
        var bank = new JsonGlyphBank(_storePath);
        bank.Capture(Sample("x"));
        bank.Capture(Sample("x"));
        bank.Capture(Sample("y"));

        var reloaded = new JsonGlyphBank(_storePath);

        Assert.Equal(2, reloaded.Samples("x").Count);
        Assert.Single(reloaded.Samples("y"));
        Assert.False(reloaded.Has("z"));
    }

    [Fact]
    public void UnknownSymbol_ReturnsEmpty()
    {
        var bank = new JsonGlyphBank(_storePath);
        bank.Capture(Sample("3"));

        Assert.Empty(bank.Samples("nope"));
    }

    private static void AssertSameGlyph(GlyphSample expected, GlyphSample actual)
    {
        Assert.Equal(expected.Symbol, actual.Symbol);
        Assert.Equal(expected.DeviceClass, actual.DeviceClass);
        Assert.Equal(expected.ConsentToShare, actual.ConsentToShare);
        Assert.Equal(expected.CapturedAt, actual.CapturedAt);
        Assert.Equal(expected.Strokes.Count, actual.Strokes.Count);
        for (int i = 0; i < expected.Strokes.Count; i++)
        {
            Assert.Equal(expected.Strokes[i].Id, actual.Strokes[i].Id);
            // StrokeSample is a record struct → value equality checks each field bit-exact.
            Assert.True(expected.Strokes[i].Samples.SequenceEqual(actual.Strokes[i].Samples));
        }
    }

    private static GlyphSample Sample(string symbol, string deviceClass = "unknown") => new(
        symbol,
        new[]
        {
            new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(1.5, -2.25, TimeSpan.FromMilliseconds(16), 0.4),
                new StrokeSample(3.0, 4.75, TimeSpan.FromMilliseconds(32), 0.8),
            }),
        },
        DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123),
        deviceClass);
}
