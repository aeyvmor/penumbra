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
    public void CloseWrittenFraction_KeepsBridgeBarSeparateFromSubstantialInkOnBothSides()
    {
        // Real mouse handwriting commonly puts the bar within the ordinary same-symbol merge radius.
        // The old union-find therefore collapsed numerator + bar + denominator into one high-confidence
        // division glyph before the spatial parser could ever see a fraction.
        Stroke numerator = Line(new[] { (40.0, 0.0), (55.0, 60.0) });
        Stroke bar = HLine(0, 68, 100);
        Stroke denominator = Line(new[] { (45.0, 76.0), (65.0, 136.0) });

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { numerator, bar, denominator });

        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Strokes));
        Assert.Same(bar, groups.Single(group => group.Strokes.Contains(bar)).Strokes.Single());
    }

    [Fact]
    public void DivisionSign_WithSmallDots_RemainsOneSymbol()
    {
        Stroke topDot = BoxStroke(17, 0, 8, 8);
        Stroke bar = HLine(0, 10, 42);
        Stroke bottomDot = BoxStroke(17, 12, 8, 8);

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { topDot, bar, bottomDot });

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Strokes.Count);
    }

    [Fact]
    public void TwoStrokeFive_TopBarStillMergesWithBody()
    {
        Stroke body = Line(new[] { (4.0, 4.0), (0.0, 24.0), (30.0, 40.0) });
        Stroke topBar = HLine(0, 0, 30);

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[] { body, topBar });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void CloseSameColumnCrossGlyphs_DoNotChainMergeAcrossRows()
    {
        Stroke[] upper =
        {
            Line(new[] { (0.0, 0.0), (40.0, 40.0) }),
            Line(new[] { (40.0, 0.0), (0.0, 40.0) }),
        };
        Stroke[] lower =
        {
            Line(new[] { (0.0, 63.0), (40.0, 103.0) }),
            Line(new[] { (40.0, 63.0), (0.0, 103.0) }),
        };

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(upper.Concat(lower).ToArray());

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Equal(2, group.Strokes.Count));
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

    private static Stroke BoxStroke(double x, double y, double width, double height) =>
        Line(new[]
        {
            (x, y),
            (x + width, y),
            (x + width, y + height),
            (x, y + height),
            (x, y),
        });

    private static Stroke Line(IEnumerable<(double X, double Y)> points) =>
        new(Guid.NewGuid(), points.Select(p => new StrokeSample(p.X, p.Y, TimeSpan.Zero, 0.5)).ToList());
}
