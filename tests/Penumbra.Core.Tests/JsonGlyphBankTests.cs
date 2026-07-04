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

    [Fact]
    public void Sample_RejectsMultiSymbolOutliers_ForRealThreePopulation()
    {
        var bank = new JsonGlyphBank(_storePath);

        // Legit single-stroke "3"s (aspects from the real user bank).
        long ms = 0;
        foreach (double aspect in new[] { 0.73, 0.76, 0.5, 0.77, 0.69, 0.56, 0.6, 0.66 })
        {
            bank.Capture(Glyph("3", width: aspect * 100, height: 100, strokes: 1, capturedMs: ms++));
        }

        // Poison: two adjacent written symbols banked as one "3" — wide (aspect > 1.1) and two strokes.
        GlyphSample poisonWide = Glyph("3", width: 117, height: 100, strokes: 2, capturedMs: 1000);
        GlyphSample poisonWider = Glyph("3", width: 130, height: 100, strokes: 2, capturedMs: 1001);
        bank.Capture(poisonWide);
        bank.Capture(poisonWider);

        var random = new Random(4);
        for (int i = 0; i < 3000; i++)
        {
            GlyphSample drawn = bank.Sample("3", random)!;
            Assert.NotEqual(poisonWide.CapturedAt, drawn.CapturedAt);
            Assert.NotEqual(poisonWider.CapturedAt, drawn.CapturedAt);
        }

        // Nothing was deleted — the raw corpus view (ADR-0006) still holds all 10.
        Assert.Equal(10, bank.Samples("3").Count);
    }

    [Fact]
    public void Sample_KeepsDegenerateDashes()
    {
        var bank = new JsonGlyphBank(_storePath);

        // Real "-" ink: height ~= 0, so raw aspect is astronomical; the epsilon floor collapses them to a
        // common value so they cluster instead of each looking like an outlier.
        long ms = 0;
        foreach (double width in new[] { 215.0, 200.0, 304.0, 57.0, 40.0 })
        {
            bank.Capture(Glyph("-", width: width, height: 0, strokes: 1, capturedMs: ms++));
        }

        var random = new Random(5);
        var seen = new HashSet<DateTimeOffset>();
        for (int i = 0; i < 3000; i++)
        {
            GlyphSample? drawn = bank.Sample("-", random);
            Assert.NotNull(drawn);
            seen.Add(drawn!.CapturedAt);
        }

        Assert.Equal(5, seen.Count); // every legit dash stays sampleable
    }

    [Fact]
    public void Sample_KeepsVerticalBarOnes()
    {
        var bank = new JsonGlyphBank(_storePath);

        // "1" written as bare vertical bars: width ~= 0. Floor keeps them comparable rather than filtered.
        long ms = 0;
        foreach (double width in new[] { 2.0, 4.0, 6.0, 3.0 })
        {
            bank.Capture(Glyph("1", width: width, height: 100, strokes: 1, capturedMs: ms++));
        }

        var random = new Random(6);
        var seen = new HashSet<DateTimeOffset>();
        for (int i = 0; i < 3000; i++)
        {
            seen.Add(bank.Sample("1", random)!.CapturedAt);
        }

        Assert.Equal(4, seen.Count); // all four bars survive
    }

    [Fact]
    public void Sample_BelowThreeExemplars_SkipsFiltering()
    {
        var bank = new JsonGlyphBank(_storePath);
        GlyphSample normal = Glyph("5", width: 70, height: 100, strokes: 1, capturedMs: 0);
        GlyphSample wild = Glyph("5", width: 500, height: 50, strokes: 4, capturedMs: 1); // outlier if judged

        bank.Capture(normal);
        bank.Capture(wild);

        var random = new Random(8);
        var seen = new HashSet<DateTimeOffset>();
        for (int i = 0; i < 2000; i++)
        {
            seen.Add(bank.Sample("5", random)!.CapturedAt);
        }

        Assert.Equal(2, seen.Count); // too few exemplars to distinguish poison from an unusual hand
    }

    [Fact]
    public void Sample_FilterEmptiesPool_FallsBackToNearestMedian()
    {
        var bank = new JsonGlyphBank(_storePath);

        // Bimodal aspects {1,1,5,5}: the median (3) lands in the empty gap, so every exemplar falls outside
        // the 1.6x window and the filter would remove all four. Fallback must keep the nearest-median one.
        GlyphSample nearest = Glyph("7", width: 100, height: 100, strokes: 1, capturedMs: 0); // aspect 1
        bank.Capture(nearest);
        bank.Capture(Glyph("7", width: 100, height: 100, strokes: 1, capturedMs: 1)); // aspect 1
        bank.Capture(Glyph("7", width: 500, height: 100, strokes: 1, capturedMs: 2)); // aspect 5
        bank.Capture(Glyph("7", width: 500, height: 100, strokes: 1, capturedMs: 3)); // aspect 5

        var random = new Random(9);
        var seen = new HashSet<DateTimeOffset>();
        for (int i = 0; i < 500; i++)
        {
            GlyphSample? drawn = bank.Sample("7", random);
            Assert.NotNull(drawn);
            seen.Add(drawn!.CapturedAt);
        }

        // Never null, and collapses to exactly the single nearest-median exemplar (first on a distance tie).
        Assert.Equal(nearest.CapturedAt, Assert.Single(seen));
    }

    [Fact]
    public void Capture_SkipsDuplicateStrokeIdSets()
    {
        var bank = new JsonGlyphBank(_storePath);
        Guid id = Guid.NewGuid();

        GlyphSample first = OneStroke("3", id, capturedMs: 0);
        GlyphSample reRecognized = OneStroke("3", id, capturedMs: 999); // same source stroke, later press
        bank.Capture(first);
        bank.Capture(reRecognized);
        Assert.Single(bank.Samples("3")); // re-recognizing the same page does not re-bank

        // A genuinely different stroke set is still appended.
        bank.Capture(OneStroke("3", Guid.NewGuid(), capturedMs: 1000));
        Assert.Equal(2, bank.Samples("3").Count);

        // Dedup keys on persisted data, so it holds on a fresh instance too.
        var reloaded = new JsonGlyphBank(_storePath);
        reloaded.Capture(reRecognized);
        Assert.Equal(2, reloaded.Samples("3").Count);
    }

    private static GlyphSample OneStroke(string symbol, Guid strokeId, long capturedMs) => new(
        symbol,
        new[] { new Stroke(strokeId, new[] { new StrokeSample(0, 0, TimeSpan.Zero, 0.5) }) },
        DateTimeOffset.FromUnixTimeMilliseconds(capturedMs));

    // Builds an exemplar with a given bounding box (via the first stroke) and stroke count, so outlier
    // tests can express aspect ratios and multi-stroke poison directly.
    private static GlyphSample Glyph(string symbol, double width, double height, int strokes, long capturedMs)
    {
        var list = new List<Stroke>
        {
            new(Guid.NewGuid(), new[]
            {
                new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
                new StrokeSample(width, height, TimeSpan.FromMilliseconds(10), 0.5),
            }),
        };
        for (int i = 1; i < strokes; i++)
        {
            list.Add(new Stroke(Guid.NewGuid(), new[] { new StrokeSample(width / 2, height / 2, TimeSpan.Zero, 0.5) }));
        }

        return new GlyphSample(symbol, list, DateTimeOffset.FromUnixTimeMilliseconds(capturedMs));
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

    // Same-symbol exemplars made distinguishable by CapturedAt so value equality can tell them apart. Each
    // gets a fresh stroke id so Capture's dedup (which keys on the source Stroke.Id set) does not fold them
    // into one — they model distinct writings of the same symbol, not re-recognitions of the same page.
    private static GlyphSample DistinctSample(string symbol, long capturedMs) => new(
        symbol,
        new[]
        {
            new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
            }),
        },
        DateTimeOffset.FromUnixTimeMilliseconds(capturedMs));
}
