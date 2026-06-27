using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>5a de-risking: spatial-overlap segmentation on synthetic stroke sets.</summary>
public sealed class OverlapStrokeSegmenterTests
{
    private readonly OverlapStrokeSegmenter _segmenter = new();

    [Fact]
    public void TwoSeparatedStrokes_AreTwoGroups()
    {
        Stroke left = VLine(0, 0, 20);
        Stroke right = VLine(40, 0, 20);   // a wide gap to the right

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { left, right });

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void CrossingStrokes_AsPlus_AreOneGroup()
    {
        Stroke horizontal = HLine(0, 10, 20);
        Stroke vertical = VLine(10, 0, 20);

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { horizontal, vertical });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void TwoCloseHorizontalBars_AsEquals_AreOneGroup()
    {
        Stroke top = HLine(0, 0, 20);
        Stroke bottom = HLine(0, 4, 20);   // same x-extent, a small vertical gap

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { top, bottom });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void Expression_TwoPlusTwoEquals_AreFourGroupsLeftToRight()
    {
        Stroke two1 = VLine(0, 0, 20);     // 2
        Stroke plusH = HLine(35, 10, 20);  // +  (two crossing strokes)
        Stroke plusV = VLine(45, 0, 20);
        Stroke two2 = VLine(80, 0, 20);    // 2
        Stroke eqTop = HLine(115, 6, 20);  // =  (two stacked bars)
        Stroke eqBot = HLine(115, 12, 20);

        IReadOnlyList<StrokeGroup> groups =
            _segmenter.Segment(new[] { two1, plusH, plusV, two2, eqTop, eqBot });

        Assert.Equal(4, groups.Count);
        Assert.Single(groups[0].Strokes);          // 2
        Assert.Equal(2, groups[1].Strokes.Count);  // +
        Assert.Single(groups[2].Strokes);          // 2
        Assert.Equal(2, groups[3].Strokes.Count);  // =
        for (int i = 1; i < groups.Count; i++)
        {
            Assert.True(groups[i - 1].Bounds.X <= groups[i].Bounds.X, "groups must be ordered left-to-right");
        }
    }

    [Fact]
    public void EmptyInput_NoGroups() => Assert.Empty(_segmenter.Segment(Array.Empty<Stroke>()));

    [Fact]
    public void EmptyStrokesAreIgnored()
    {
        Stroke blank = new(Guid.NewGuid(), Array.Empty<StrokeSample>());
        Stroke real = VLine(0, 0, 20);

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { blank, real });

        Assert.Single(groups);
        Assert.Single(groups[0].Strokes);
    }

    private static Stroke VLine(double x, double y0, double length) =>
        Line(Enumerable.Range(0, 11).Select(i => (x, y0 + length * i / 10.0)));

    private static Stroke HLine(double x0, double y, double length) =>
        Line(Enumerable.Range(0, 11).Select(i => (x0 + length * i / 10.0, y)));

    private static Stroke Line(IEnumerable<(double X, double Y)> points) =>
        new(Guid.NewGuid(), points.Select(p => new StrokeSample(p.X, p.Y, TimeSpan.Zero, 0.5)).ToList());
}
