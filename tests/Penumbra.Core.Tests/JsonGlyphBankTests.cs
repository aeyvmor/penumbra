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

    [Fact]
    public void Sample_UnknownSymbol_ReturnsNull()
    {
        var bank = new JsonGlyphBank(_storePath);
        bank.Capture(Sample("3"));

        Assert.Null(bank.Sample("nope", new Random(1)));
    }

    [Fact]
    public void Sample_SingleExemplar_AlwaysReturnsIt()
    {
        var bank = new JsonGlyphBank(_storePath);
        GlyphSample only = DistinctSample("x", capturedMs: 100);
        bank.Capture(only);

        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(only, bank.Sample("x", random));
        }
    }

    [Fact]
    public void Sample_ReturnedGlyphIsAlwaysOneOfTheBanked()
    {
        var bank = new JsonGlyphBank(_storePath);
        for (int i = 0; i < 5; i++)
        {
            bank.Capture(DistinctSample("x", capturedMs: i));
        }

        IReadOnlyList<GlyphSample> banked = bank.Samples("x");
        var random = new Random(11);
        for (int i = 0; i < 1_000; i++)
        {
            GlyphSample? drawn = bank.Sample("x", random);
            Assert.NotNull(drawn);
            Assert.Contains(drawn, banked);
        }
    }

    [Fact]
    public void Sample_RecencyWeighting_PrefersNewestOverOldest()
    {
        var bank = new JsonGlyphBank(_storePath);
        const int exemplars = 5;
        for (int i = 0; i < exemplars; i++)
        {
            bank.Capture(DistinctSample("x", capturedMs: i)); // capturedMs 0 = oldest, 4 = newest
        }

        // Exemplars are identified by CapturedAt: record equality on the Strokes list is reference-based,
        // so a freshly-built comparison instance would never match a banked one.
        DateTimeOffset oldest = DateTimeOffset.FromUnixTimeMilliseconds(0);
        DateTimeOffset newest = DateTimeOffset.FromUnixTimeMilliseconds(exemplars - 1);

        int newestHits = 0, oldestHits = 0;
        var random = new Random(20260703); // seeded → this assertion is deterministic
        for (int i = 0; i < 10_000; i++)
        {
            GlyphSample drawn = bank.Sample("x", random)!;
            if (drawn.CapturedAt == newest) newestHits++;
            else if (drawn.CapturedAt == oldest) oldestHits++;
        }

        // Linear weights 1..5 make the newest ~5x as likely as the oldest; assert only the strict ordering.
        Assert.True(newestHits > oldestHits,
            $"expected newest to be drawn more often than oldest, got newest={newestHits} oldest={oldestHits}");
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

    // Same-symbol exemplars made distinguishable by CapturedAt so value equality can tell them apart.
    private static GlyphSample DistinctSample(string symbol, long capturedMs) => new(
        symbol,
        new[]
        {
            new Stroke(Guid.Empty, new[]
            {
                new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
            }),
        },
        DateTimeOffset.FromUnixTimeMilliseconds(capturedMs));
}
