using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// 3.9d passive capture: only confident tokens become bank exemplars, and each is materialized from the
/// exact source strokes it aligns to (Seam 1).
/// </summary>
public sealed class GlyphCaptureTests
{
    private const double Threshold = 0.55;

    [Fact]
    public void SkipsLowConfidenceTokens()
    {
        Stroke keep = StrokeAt(0);
        Stroke drop = StrokeAt(10);
        var tokens = new[]
        {
            Token("3", 0.9, keep.Id),
            Token("x", 0.20, drop.Id),   // below threshold — must not be banked
        };

        IReadOnlyList<GlyphSample> samples =
            GlyphCapture.Collect(tokens, new[] { keep, drop }, Threshold, DateTimeOffset.UnixEpoch);

        GlyphSample only = Assert.Single(samples);
        Assert.Equal("3", only.Symbol);
        Assert.Equal(keep.Id, Assert.Single(only.Strokes).Id);
    }

    [Fact]
    public void ResolvesSourceStrokesById_AndTagsDeviceClass()
    {
        Stroke s1 = StrokeAt(0);
        Stroke s2 = StrokeAt(5);
        var tokens = new[] { Token("+", 0.8, s1.Id, s2.Id) };

        IReadOnlyList<GlyphSample> samples = GlyphCapture.Collect(
            tokens, new[] { s1, s2 }, Threshold, DateTimeOffset.UnixEpoch, deviceClass: "mouse");

        GlyphSample sample = Assert.Single(samples);
        Assert.Equal(2, sample.Strokes.Count);
        Assert.Equal("mouse", sample.DeviceClass);
        Assert.False(sample.ConsentToShare);   // local-only by default
    }

    [Fact]
    public void SkipsTokensWhoseStrokesAreMissing()
    {
        var tokens = new[] { Token("3", 0.9, Guid.NewGuid()) };   // id not present in the stroke set

        IReadOnlyList<GlyphSample> samples =
            GlyphCapture.Collect(tokens, Array.Empty<Stroke>(), Threshold, DateTimeOffset.UnixEpoch);

        Assert.Empty(samples);
    }

    [Fact]
    public void ExactlyAtThreshold_IsCaptured()
    {
        Stroke s = StrokeAt(0);

        IReadOnlyList<GlyphSample> samples =
            GlyphCapture.Collect(new[] { Token("x", Threshold, s.Id) }, new[] { s }, Threshold, DateTimeOffset.UnixEpoch);

        Assert.Single(samples);
    }

    private static RecognizedToken Token(string label, double confidence, params Guid[] strokeIds) =>
        new(label, strokeIds, default, confidence);

    private static Stroke StrokeAt(double x) =>
        new(Guid.NewGuid(), new[] { new StrokeSample(x, 0, TimeSpan.Zero, 0.5) });
}
