using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>
/// Phase 4.5d: "did the tap land on the answer" is a geometry decision, so it is proven here — the
/// canvas only forwards coordinates.
/// </summary>
public sealed class AnswerHitTesterTests
{
    [Fact]
    public void PointOnAStrokeSample_Hits()
    {
        var strokes = new[] { HLine(y: 50, x0: 10, x1: 60) };

        Assert.True(AnswerHitTester.HitTest(strokes, 30, 50, tolerance: 4));
    }

    [Fact]
    public void PointBetweenSamples_StillHits_BecauseSegmentsAreTested()
    {
        // Two samples 50 apart; the midpoint is nowhere near either SAMPLE but on the SEGMENT.
        var stroke = new Stroke(Guid.NewGuid(), new List<StrokeSample>
        {
            new(0, 0, TimeSpan.Zero, 0.5),
            new(50, 0, TimeSpan.Zero, 0.5),
        });

        Assert.True(AnswerHitTester.HitTest(new[] { stroke }, 25, 2, tolerance: 4));
    }

    [Fact]
    public void PointBeyondTolerance_Misses()
    {
        var strokes = new[] { HLine(y: 50, x0: 10, x1: 60) };

        Assert.False(AnswerHitTester.HitTest(strokes, 30, 60, tolerance: 4));   // 10 px above, tol 4
        Assert.False(AnswerHitTester.HitTest(strokes, 80, 50, tolerance: 4));   // past the segment end
    }

    [Fact]
    public void SingleSampleDot_HitsWithinTolerance()
    {
        var dot = new Stroke(Guid.NewGuid(), new List<StrokeSample> { new(20, 20, TimeSpan.Zero, 0.5) });

        Assert.True(AnswerHitTester.HitTest(new[] { dot }, 22, 21, tolerance: 4));
        Assert.False(AnswerHitTester.HitTest(new[] { dot }, 30, 30, tolerance: 4));
    }

    [Fact]
    public void EmptyStrokes_NeverHit()
    {
        Assert.False(AnswerHitTester.HitTest(Array.Empty<Stroke>(), 0, 0, tolerance: 100));
    }

    private static Stroke HLine(double y, double x0, double x1) => new(
        Guid.NewGuid(),
        Enumerable.Range(0, 11).Select(i => new StrokeSample(x0 + (x1 - x0) * i / 10.0, y, TimeSpan.Zero, 0.5)).ToList());
}
