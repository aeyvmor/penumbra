using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 4.5b corpus-poison guard, half 1: live mode re-reads the same ink on every pen-lift, and the
/// dedup overload must guarantee one physical glyph banks exactly once. (Half 2 — never banking a
/// partial glyph — is the view-model's "bank only on computed '='-reads" rule, exercised in dogfood.)
/// </summary>
public sealed class GlyphCaptureDedupTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameInk_SecondCollect_YieldsNothing()
    {
        Stroke stroke = Stroke(0);
        var tokens = new[] { Token("2", 0.95, stroke.Id) };
        var banked = new HashSet<string>();

        IReadOnlyList<GlyphSample> first = GlyphCapture.Collect(tokens, new[] { stroke }, 0.8, T0, banked);
        IReadOnlyList<GlyphSample> second = GlyphCapture.Collect(tokens, new[] { stroke }, 0.8, T0, banked);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(banked);
    }

    [Fact]
    public void NewInk_StillCollected_WhileOldInkStaysSkipped()
    {
        Stroke old = Stroke(0);
        Stroke fresh = Stroke(60);
        var banked = new HashSet<string>();

        GlyphCapture.Collect(new[] { Token("2", 0.95, old.Id) }, new[] { old }, 0.8, T0, banked);
        IReadOnlyList<GlyphSample> second = GlyphCapture.Collect(
            new[] { Token("2", 0.95, old.Id), Token("7", 0.95, fresh.Id) },
            new[] { old, fresh }, 0.8, T0, banked);

        GlyphSample sample = Assert.Single(second);
        Assert.Equal("7", sample.Symbol);
        Assert.Equal(2, banked.Count);
    }

    [Fact]
    public void StrokeSetKey_IsOrderInsensitive()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.Equal(GlyphCapture.StrokeSetKey(new[] { a, b }), GlyphCapture.StrokeSetKey(new[] { b, a }));
        Assert.NotEqual(GlyphCapture.StrokeSetKey(new[] { a }), GlyphCapture.StrokeSetKey(new[] { a, b }));
    }

    [Fact]
    public void BelowThresholdOrUnbankable_NeverEnterTheKeySet()
    {
        // Rejected samples must not claim their key: if the same ink later passes the bar
        // (e.g. a cleaner re-read), it must still be bankable.
        Stroke shaky = Stroke(0);
        Stroke letter = Stroke(60);
        var banked = new HashSet<string>();

        IReadOnlyList<GlyphSample> collected = GlyphCapture.Collect(
            new[] { Token("2", 0.5, shaky.Id), Token("y", 0.99, letter.Id) },
            new[] { shaky, letter }, 0.8, T0, banked);

        Assert.Empty(collected);
        Assert.Empty(banked);

        // The same physical '2', now read confidently, banks fine.
        IReadOnlyList<GlyphSample> retry = GlyphCapture.Collect(
            new[] { Token("2", 0.9, shaky.Id) }, new[] { shaky }, 0.8, T0, banked);
        Assert.Single(retry);
    }

    private static RecognizedToken Token(string latex, double confidence, params Guid[] strokeIds) =>
        new(latex, strokeIds, new InkBounds(0, 0, 10, 10), confidence);

    private static Stroke Stroke(double x) => new(
        Guid.NewGuid(),
        Enumerable.Range(0, 5).Select(i => new StrokeSample(x, i * 5.0, TimeSpan.Zero, 0.5)).ToList());
}
