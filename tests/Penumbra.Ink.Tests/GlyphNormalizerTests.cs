using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>
/// Em-box normalization (Phase 4b): uniform aspect-preserving fit into [0,1]×[0,1], degenerate ink handled
/// without dividing by zero, and Times/Pressures/Ids/order carried through untouched.
/// </summary>
public sealed class GlyphNormalizerTests
{
    private const double Eps = 1e-9;

    private static Stroke Stroke(Guid id, params StrokeSample[] samples) => new(id, samples);

    private static StrokeSample At(double x, double y) => new(x, y, TimeSpan.Zero, 0);

    [Fact]
    public void SquareInk_MapsCornersToUnitBox()
    {
        // A 10×10 square from (5,5)..(15,15): larger dim = 10, so it fills the whole box exactly.
        var input = new[] { Stroke(Guid.NewGuid(), At(5, 5), At(15, 15)) };

        IReadOnlyList<Stroke> output = GlyphNormalizer.ToEmBox(input);

        StrokeSample lo = output[0].Samples[0];
        StrokeSample hi = output[0].Samples[1];
        Assert.Equal(0.0, lo.X, Eps);
        Assert.Equal(0.0, lo.Y, Eps);
        Assert.Equal(1.0, hi.X, Eps);
        Assert.Equal(1.0, hi.Y, Eps);
    }

    [Fact]
    public void WideGlyph_PreservesAspectAndCentersMinorAxis()
    {
        // 2:1 wide (width 20, height 10): width spans [0,1], height 0.5 tall centered → y in [0.25,0.75].
        var input = new[] { Stroke(Guid.NewGuid(), At(0, 0), At(20, 10)) };

        IReadOnlyList<Stroke> output = GlyphNormalizer.ToEmBox(input);

        StrokeSample lo = output[0].Samples[0];
        StrokeSample hi = output[0].Samples[1];
        Assert.Equal(0.0, lo.X, Eps);
        Assert.Equal(0.25, lo.Y, Eps);
        Assert.Equal(1.0, hi.X, Eps);
        Assert.Equal(0.75, hi.Y, Eps);
    }

    [Fact]
    public void TallGlyph_PreservesAspectAndCentersMinorAxis()
    {
        // 1:2 tall (width 10, height 20): height spans [0,1], width 0.5 wide centered → x in [0.25,0.75].
        var input = new[] { Stroke(Guid.NewGuid(), At(0, 0), At(10, 20)) };

        IReadOnlyList<Stroke> output = GlyphNormalizer.ToEmBox(input);

        StrokeSample lo = output[0].Samples[0];
        StrokeSample hi = output[0].Samples[1];
        Assert.Equal(0.25, lo.X, Eps);
        Assert.Equal(0.0, lo.Y, Eps);
        Assert.Equal(0.75, hi.X, Eps);
        Assert.Equal(1.0, hi.Y, Eps);
    }

    [Fact]
    public void SinglePoint_MapsToCenter()
    {
        var input = new[] { Stroke(Guid.NewGuid(), At(42, -7)) };

        IReadOnlyList<Stroke> output = GlyphNormalizer.ToEmBox(input);

        StrokeSample p = output[0].Samples[0];
        Assert.Equal(0.5, p.X, Eps);
        Assert.Equal(0.5, p.Y, Eps);
    }

    [Fact]
    public void HorizontalBar_SpansXAtVerticalCenter()
    {
        // Height 0, width > 0: x must span [0,1]; the flat y-axis centers at 0.5.
        var input = new[] { Stroke(Guid.NewGuid(), At(3, 9), At(8, 9), At(13, 9)) };

        IReadOnlyList<Stroke> output = GlyphNormalizer.ToEmBox(input);

        StrokeSample[] pts = output[0].Samples.ToArray();
        Assert.Equal(0.0, pts[0].X, Eps);
        Assert.Equal(0.5, pts[1].X, Eps);
        Assert.Equal(1.0, pts[2].X, Eps);
        Assert.All(pts, p => Assert.Equal(0.5, p.Y, Eps));
    }

    [Fact]
    public void PreservesTimesPressuresIdsAndOrder()
    {
        var id0 = Guid.NewGuid();
        var id1 = Guid.NewGuid();
        var input = new[]
        {
            new Stroke(id0, new[]
            {
                new StrokeSample(0, 0, TimeSpan.FromMilliseconds(5), 0.3),
                new StrokeSample(10, 10, TimeSpan.FromMilliseconds(9), 0.7),
            }),
            new Stroke(id1, new[]
            {
                new StrokeSample(2, 2, TimeSpan.FromMilliseconds(12), 0.9),
            }),
        };

        IReadOnlyList<Stroke> output = GlyphNormalizer.ToEmBox(input);

        Assert.Equal(id0, output[0].Id);
        Assert.Equal(id1, output[1].Id);
        Assert.Equal(TimeSpan.FromMilliseconds(5), output[0].Samples[0].Time);
        Assert.Equal(0.3, output[0].Samples[0].Pressure);
        Assert.Equal(TimeSpan.FromMilliseconds(9), output[0].Samples[1].Time);
        Assert.Equal(0.7, output[0].Samples[1].Pressure);
        Assert.Equal(TimeSpan.FromMilliseconds(12), output[1].Samples[0].Time);
        Assert.Equal(0.9, output[1].Samples[0].Pressure);
    }

    [Fact]
    public void DoesNotMutateInput()
    {
        var input = new[] { Stroke(Guid.NewGuid(), At(5, 5), At(15, 25)) };

        GlyphNormalizer.ToEmBox(input);

        Assert.Equal(5, input[0].Samples[0].X);
        Assert.Equal(5, input[0].Samples[0].Y);
        Assert.Equal(15, input[0].Samples[1].X);
        Assert.Equal(25, input[0].Samples[1].Y);
    }

    [Fact]
    public void IsIdempotent()
    {
        var input = new[]
        {
            Stroke(Guid.NewGuid(), At(3, 4), At(23, 14), At(13, 9)),
        };

        IReadOnlyList<Stroke> once = GlyphNormalizer.ToEmBox(input);
        IReadOnlyList<Stroke> twice = GlyphNormalizer.ToEmBox(once);

        for (int j = 0; j < once[0].Samples.Count; j++)
        {
            Assert.Equal(once[0].Samples[j].X, twice[0].Samples[j].X, Eps);
            Assert.Equal(once[0].Samples[j].Y, twice[0].Samples[j].Y, Eps);
        }
    }
}
