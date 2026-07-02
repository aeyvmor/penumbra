using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 3.9g: regression fixtures ported from real dogfood ink. On large mouse writing
/// (~150-190px symbols) the old segmenter gaps (gapX 0.4, gapY 1.2 of the reference size) chain-merged
/// SEPARATE symbols into one blob, which the CNN then confidently mislabelled — silent wrong answers
/// ('5-1=' read as '5=', '27' read as '2'). The stroke coordinates below trace the bounding boxes of
/// the ink in those exact screenshots (only the boxes matter to <see cref="OverlapStrokeSegmenter"/>).
///
/// Two-sided validation: the over-merge cases must now split correctly, AND genuine multi-stroke
/// symbols (=, +, 7, 4, ÷) must still merge into one group.
/// </summary>
public sealed class SegmenterRealInkRegressionTests
{
    private readonly OverlapStrokeSegmenter _segmenter = new();

    // ---- over-merge regressions (must now SPLIT) -------------------------------------------------

    [Fact]
    public void Shot11_27_SplitsIntoTwoDigits()
    {
        // Real gap between the '2' and the '7' bar was ~15px — smaller than the old swallow radius.
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(200, 260, 335, 380),   // 2
            Line(350, 235, 420, 240),   // 7 top bar (15px right of the 2)
            Line(415, 232, 360, 425),   // 7 stem
        });

        Assert.Equal(2, groups.Count);
        Assert.Single(groups[0].Strokes);          // 2
        Assert.Equal(2, groups[1].Strokes.Count);  // 7 (bar + stem)
        AssertLeftToRight(groups);
    }

    [Fact]
    public void Shot9_5Minus1Equals_SplitsIntoFourSymbols()
    {
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(150, 275, 155, 400),   // 5 stem
            Line(150, 275, 290, 280),   // 5 top bar
            Line(355, 347, 390, 349),   // minus
            Line(410, 285, 445, 275),   // 1 flag
            Line(445, 275, 425, 390),   // 1 stem
            Line(490, 315, 555, 318),   // = top
            Line(490, 345, 580, 350),   // = bottom
        });

        Assert.Equal(4, groups.Count);              // 5, -, 1, =
        Assert.Equal(2, groups[0].Strokes.Count);   // 5
        Assert.Single(groups[1].Strokes);           // minus
        Assert.Equal(2, groups[2].Strokes.Count);   // 1
        Assert.Equal(2, groups[3].Strokes.Count);   // =
        AssertLeftToRight(groups);
    }

    [Fact]
    public void Shot4_2Bar_Plus7Equals_SplitsIntoFiveSymbols()
    {
        // The '+' bar overlaps the '7' bar in x; only the tightened vertical gap keeps them apart.
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(180, 200, 290, 320),   // 2
            Line(305, 170, 338, 330),   // slanted | (a drawn 1)
            Line(350, 225, 400, 228),   // + horizontal
            Line(372, 197, 375, 258),   // + vertical
            Line(388, 158, 480, 165),   // 7 bar
            Line(475, 160, 430, 295),   // 7 stem
            Line(495, 213, 545, 217),   // = top
            Line(498, 233, 543, 238),   // = bottom
        });

        Assert.Equal(5, groups.Count);              // 2, |, +, 7, =
        Assert.Single(groups[0].Strokes);           // 2
        Assert.Single(groups[1].Strokes);           // |
        Assert.Equal(2, groups[2].Strokes.Count);   // +
        Assert.Equal(2, groups[3].Strokes.Count);   // 7
        Assert.Equal(2, groups[4].Strokes.Count);   // =
        AssertLeftToRight(groups);
    }

    [Fact]
    public void Shot2_Sqrt9Equals_SeparatesEqualsFromRadicalCluster()
    {
        // The \sqrt covering its radicand (the radical strokes + the 9 under the vinculum) is a 2-D
        // grammar concern deferred to post-M2; here we only require that the trailing '=' breaks away
        // from the radical cluster. So: exactly 2 groups — {radical + 9} and {=}.
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(270, 285, 310, 360),   // radical tick
            Line(310, 360, 335, 228),   // radical rise
            Line(335, 228, 450, 232),   // radical top bar (vinculum)
            Line(340, 250, 400, 355),   // the 9, under the vinculum
            Line(490, 290, 560, 295),   // = top
            Line(495, 320, 580, 328),   // = bottom
        });

        Assert.Equal(2, groups.Count);
        Assert.Equal(4, groups[0].Strokes.Count);   // radical + radicand (split deferred: post-M2)
        Assert.Equal(2, groups[1].Strokes.Count);   // =
        AssertLeftToRight(groups);
    }

    // ---- positive: genuine multi-stroke symbols must still MERGE ---------------------------------

    [Fact]
    public void Equals_TwoStackedBars_StayOneGroup()
    {
        // Stacked bars: same x-extent, dy ≈ 0.33x the bar width — well inside the vertical window.
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(0, 0, 60, 0),
            Line(0, 20, 60, 20),
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void Plus_CrossingStrokes_StayOneGroup()
    {
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(0, 20, 40, 20),    // horizontal
            Line(20, 0, 20, 40),    // vertical (crosses it)
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void Seven_BarTouchingStem_StayOneGroup()
    {
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(0, 0, 40, 0),      // top bar
            Line(38, 0, 20, 60),    // stem, starting at the bar's right end
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void Four_TwoOverlappingStrokes_StayOneGroup()
    {
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(0, 30, 40, 30),    // horizontal cross-bar
            Line(30, 0, 30, 50),    // stem crossing it
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    [Fact]
    public void Divide_BarWithDotAbove_StayOneGroup()
    {
        // ÷ : a bar with a dot floating above it, dy ≈ 0.25x the symbol size — inside the window.
        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(new[]
        {
            Line(0, 20, 40, 20),    // bar
            Line(18, 6, 22, 10),    // dot above (small stroke)
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Strokes.Count);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static void AssertLeftToRight(IReadOnlyList<StrokeGroup> groups)
    {
        for (int i = 1; i < groups.Count; i++)
        {
            Assert.True(groups[i - 1].Bounds.X <= groups[i].Bounds.X, "groups must be ordered left-to-right");
        }
    }

    // A straight stroke from (x1,y1) to (x2,y2); only its bounding box drives the segmenter.
    private static Stroke Line(double x1, double y1, double x2, double y2)
    {
        const int n = 12;
        var samples = new List<StrokeSample>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            samples.Add(new StrokeSample(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, TimeSpan.Zero, 0.5));
        }
        return new Stroke(Guid.NewGuid(), samples);
    }
}
