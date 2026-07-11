using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>
/// <see cref="StrokeTransformer"/>: scale-about-origin then translate, on COPIES with fresh ids —
/// duplicate ids would corrupt the .pen v3 load cache and Seam-1 alignment.
/// </summary>
public sealed class StrokeTransformerTests
{
    [Fact]
    public void PureTranslationShiftsEverySample()
    {
        Stroke source = Make((0, 0), (4, 6));

        IReadOnlyList<Stroke> result = StrokeTransformer.Transform(new[] { source }, dx: 10, dy: -5);

        Stroke moved = Assert.Single(result);
        Assert.Equal(new[] { (10.0, -5.0), (14.0, 1.0) }, moved.Samples.Select(s => (s.X, s.Y)));
    }

    [Fact]
    public void ScalesAboutTheOriginPointThenTranslates()
    {
        // (4, 6) about origin (2, 2) at scale 2: 2 + (4−2)·2 = 6, 2 + (6−2)·2 = 10; then +(1, −1).
        Stroke source = Make((4, 6));

        IReadOnlyList<Stroke> result = StrokeTransformer.Transform(
            new[] { source }, dx: 1, dy: -1, scale: 2, originX: 2, originY: 2);

        StrokeSample mapped = Assert.Single(result).Samples[0];
        Assert.Equal(7.0, mapped.X, 12);
        Assert.Equal(9.0, mapped.Y, 12);
    }

    [Fact]
    public void AssignsFreshUniqueIds()
    {
        Stroke a = Make((0, 0));
        Stroke b = Make((1, 1));

        IReadOnlyList<Stroke> result = StrokeTransformer.Transform(new[] { a, b }, 0, 0);

        Guid[] sourceIds = { a.Id, b.Id };
        Assert.All(result, s => Assert.DoesNotContain(s.Id, sourceIds));
        Assert.Equal(result.Count, result.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void PreservesTimeAndPressureExactly()
    {
        Stroke source = Make((0, 0), (4, 6));

        Stroke moved = Assert.Single(StrokeTransformer.Transform(new[] { source }, 3, 4, scale: 1.5));

        Assert.Equal(source.Samples.Select(s => s.Time), moved.Samples.Select(s => s.Time));
        Assert.Equal(source.Samples.Select(s => s.Pressure), moved.Samples.Select(s => s.Pressure));
    }

    [Fact]
    public void EmptyInputYieldsEmptyOutput()
    {
        Assert.Empty(StrokeTransformer.Transform(Array.Empty<Stroke>(), 5, 5));
    }

    [Fact]
    public void NullInputThrows()
    {
        Assert.Throws<ArgumentNullException>(() => StrokeTransformer.Transform(null!, 0, 0));
    }

    // Distinct time/pressure per sample so preservation is provable sample-by-sample.
    private static Stroke Make(params (double X, double Y)[] points) => new(
        Guid.NewGuid(),
        points.Select((p, i) =>
            new StrokeSample(p.X, p.Y, TimeSpan.FromMilliseconds(i * 7), 0.3 + i * 0.1)).ToList());
}
