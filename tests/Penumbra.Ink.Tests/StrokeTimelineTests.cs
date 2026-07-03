using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class StrokeTimelineTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // A three-sample stroke: positions/pressures chosen so interpolation is easy to hand-check.
    // times 0/10/20 ms, so its own duration is 20 ms.
    private static Stroke Stroke3(Guid id, double x0) => new(id, new[]
    {
        new StrokeSample(x0 + 0, 0, Ms(0), 0.2),
        new StrokeSample(x0 + 10, 0, Ms(10), 0.6),
        new StrokeSample(x0 + 20, 10, Ms(20), 1.0),
    });

    [Fact]
    public void TotalDurationSumsStrokeDurationsAndDistinctGaps()
    {
        var a = Stroke3(Guid.NewGuid(), 0);   // 20 ms
        var b = Stroke3(Guid.NewGuid(), 100); // 20 ms
        var c = Stroke3(Guid.NewGuid(), 200); // 20 ms

        var timeline = new StrokeTimeline(new[] { a, b, c }, new[] { Ms(5), Ms(8) });

        // 20 + 20 + 20 draw + 5 + 8 air = 73 ms
        Assert.Equal(Ms(73), timeline.TotalDuration);
    }

    [Fact]
    public void SampleAtZeroIsEmptyWhenFirstStrokeHasDuration()
    {
        var timeline = new StrokeTimeline(new[] { Stroke3(Guid.NewGuid(), 0) });

        // A stroke with real duration starts drawing strictly after its start instant, so t=0 shows nothing.
        Assert.Empty(timeline.SampleAt(TimeSpan.Zero));
    }

    [Fact]
    public void SampleAtEndReturnsEveryStrokeWholeWithIdsPreserved()
    {
        var a = Stroke3(Guid.NewGuid(), 0);
        var b = Stroke3(Guid.NewGuid(), 100);
        var timeline = new StrokeTimeline(new[] { a, b }, new[] { Ms(7) });

        IReadOnlyList<Stroke> visible = timeline.SampleAt(timeline.TotalDuration);

        Assert.Equal(2, visible.Count);
        Assert.Equal(a.Id, visible[0].Id);
        Assert.Equal(b.Id, visible[1].Id);
        Assert.Equal(a.Samples, visible[0].Samples); // whole strokes returned intact
        Assert.Equal(b.Samples, visible[1].Samples);
    }

    [Fact]
    public void MidStrokeYieldsPartialEndingInInterpolatedSample()
    {
        var a = Stroke3(Guid.NewGuid(), 0);
        var timeline = new StrokeTimeline(new[] { a });

        // 15 ms into a 20 ms stroke: sample 1 (t=10) is fully in; the end sample is halfway from s1 to s2.
        IReadOnlyList<Stroke> visible = timeline.SampleAt(Ms(15));

        Stroke partial = Assert.Single(visible);
        Assert.Equal(a.Id, partial.Id);
        Assert.Equal(3, partial.Samples.Count);          // s0, s1, interpolated
        Assert.Equal(a.Samples[0], partial.Samples[0]);
        Assert.Equal(a.Samples[1], partial.Samples[1]);

        // Lerp(s1, s2, 0.5): X=(10+20)/2, Y=(0+10)/2, P=(0.6+1.0)/2
        StrokeSample end = partial.Samples[2];
        Assert.Equal(15, end.X, precision: 9);
        Assert.Equal(5, end.Y, precision: 9);
        Assert.Equal(0.8, end.Pressure, precision: 9);
    }

    [Fact]
    public void MidStrokeBeforeFirstInteriorSampleYieldsTwoPoints()
    {
        var a = Stroke3(Guid.NewGuid(), 0);
        var timeline = new StrokeTimeline(new[] { a });

        // 5 ms in: only s0 is fully in, end sample is halfway from s0 to s1.
        Stroke partial = Assert.Single(timeline.SampleAt(Ms(5)));

        Assert.Equal(2, partial.Samples.Count);
        Assert.Equal(a.Samples[0], partial.Samples[0]);
        StrokeSample end = partial.Samples[1];
        Assert.Equal(5, end.X, precision: 9);   // Lerp(s0, s1, 0.5) X = (0+10)/2
        Assert.Equal(0, end.Y, precision: 9);
        Assert.Equal(0.4, end.Pressure, precision: 9); // (0.2+0.6)/2
    }

    [Fact]
    public void MidGapReturnsOnlyFinishedStrokes()
    {
        var a = Stroke3(Guid.NewGuid(), 0);   // [0,20]
        var b = Stroke3(Guid.NewGuid(), 100); // starts at 25
        var timeline = new StrokeTimeline(new[] { a, b }, new[] { Ms(5) });

        // t=22 ms is inside the 20→25 air-move gap: a is done, b hasn't started, nothing partial.
        IReadOnlyList<Stroke> visible = timeline.SampleAt(Ms(22));

        Stroke finished = Assert.Single(visible);
        Assert.Equal(a.Id, finished.Id);
        Assert.Equal(a.Samples, finished.Samples);
    }

    [Fact]
    public void PartialOfSecondStrokeAccountsForItsStartOffset()
    {
        var a = Stroke3(Guid.NewGuid(), 0);   // [0,20]
        var b = Stroke3(Guid.NewGuid(), 100); // starts at 25, own duration 20
        var timeline = new StrokeTimeline(new[] { a, b }, new[] { Ms(5) });

        // t=30 ms = 5 ms into b's own timeline → end sample halfway from b.s0 to b.s1.
        IReadOnlyList<Stroke> visible = timeline.SampleAt(Ms(30));

        Assert.Equal(2, visible.Count);
        Assert.Equal(a.Samples, visible[0].Samples);     // a whole
        Stroke partialB = visible[1];
        Assert.Equal(b.Id, partialB.Id);
        Assert.Equal(2, partialB.Samples.Count);
        Assert.Equal(105, partialB.Samples[1].X, precision: 9); // (100 + 110)/2
    }

    [Fact]
    public void EmptyStrokeListHasZeroDurationAndSamplesEmpty()
    {
        var timeline = new StrokeTimeline(Array.Empty<Stroke>());

        Assert.Equal(TimeSpan.Zero, timeline.TotalDuration);
        Assert.Empty(timeline.SampleAt(TimeSpan.Zero));
        Assert.Empty(timeline.SampleAt(Ms(1000)));
        Assert.Empty(timeline.SampleAt(Ms(-1000)));
    }

    [Fact]
    public void ZeroDurationPointStrokeAppearsWholeAtItsStartInstant()
    {
        var a = Stroke3(Guid.NewGuid(), 0); // 20 ms
        var point = new Stroke(Guid.NewGuid(), new[] { new StrokeSample(50, 50, Ms(0), 0.5) });
        var timeline = new StrokeTimeline(new[] { a, point }, new[] { Ms(10) });

        // point starts (and ends) at 20 + 10 = 30 ms = TotalDuration.
        Assert.Equal(Ms(30), timeline.TotalDuration);

        // Just before its start it is absent; exactly at its start it is whole.
        Assert.Single(timeline.SampleAt(Ms(29)));
        IReadOnlyList<Stroke> atStart = timeline.SampleAt(Ms(30));
        Assert.Equal(2, atStart.Count);
        Assert.Equal(point.Id, atStart[1].Id);
        Assert.Equal(point.Samples, atStart[1].Samples);
    }

    [Fact]
    public void EqualTimeSamplesFormZeroDurationStroke()
    {
        // All samples share one timestamp: zero own-duration, so it snaps in whole at its start.
        var flat = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(0, 0, Ms(5), 0.5),
            new StrokeSample(3, 0, Ms(5), 0.5),
            new StrokeSample(6, 0, Ms(5), 0.5),
        });
        var timeline = new StrokeTimeline(new[] { flat });

        Assert.Equal(TimeSpan.Zero, timeline.TotalDuration);
        IReadOnlyList<Stroke> visible = timeline.SampleAt(TimeSpan.Zero);
        Assert.Equal(flat.Samples, Assert.Single(visible).Samples);
    }

    [Fact]
    public void NonMonotonicSampleTimesDoNotRewindTheClock()
    {
        // s1's timestamp goes backwards; cumulative max keeps duration = last-first over the running max.
        var wobbly = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(0, 0, Ms(0), 0.5),
            new StrokeSample(10, 0, Ms(4), 0.5),
            new StrokeSample(20, 0, Ms(2), 0.5), // backwards: shares s1's instant (4 ms) via cumulative max
            new StrokeSample(30, 0, Ms(10), 0.5),
        });
        var timeline = new StrokeTimeline(new[] { wobbly });

        // Running max times: 0,4,4,10 → duration 10 ms, no crash, monotonic schedule.
        Assert.Equal(Ms(10), timeline.TotalDuration);
        // Whole at the end with all original samples intact.
        Assert.Equal(wobbly.Samples, Assert.Single(timeline.SampleAt(Ms(10))).Samples);
    }

    [Fact]
    public void NegativeTimeClampsToStartAndOverrunClampsToEnd()
    {
        var a = Stroke3(Guid.NewGuid(), 0);
        var b = Stroke3(Guid.NewGuid(), 100);
        var timeline = new StrokeTimeline(new[] { a, b }, new[] { Ms(5) });

        Assert.Empty(timeline.SampleAt(Ms(-500)));                       // clamps to t=0 → nothing yet
        IReadOnlyList<Stroke> over = timeline.SampleAt(Ms(100_000));      // clamps to TotalDuration → everything
        Assert.Equal(2, over.Count);
        Assert.Equal(a.Samples, over[0].Samples);
        Assert.Equal(b.Samples, over[1].Samples);
    }

    [Fact]
    public void DeterministicForRepeatedCalls()
    {
        var timeline = new StrokeTimeline(new[] { Stroke3(Guid.NewGuid(), 0), Stroke3(Guid.NewGuid(), 100) }, new[] { Ms(5) });

        IReadOnlyList<Stroke> first = timeline.SampleAt(Ms(30));
        IReadOnlyList<Stroke> second = timeline.SampleAt(Ms(30));

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Id, second[i].Id);
            Assert.Equal(first[i].Samples, second[i].Samples);
        }
    }

    [Fact]
    public void UniformAirMoveOverloadSpacesStrokesEvenly()
    {
        var a = Stroke3(Guid.NewGuid(), 0);   // 20 ms
        var b = Stroke3(Guid.NewGuid(), 100); // 20 ms
        var c = Stroke3(Guid.NewGuid(), 200); // 20 ms

        var timeline = new StrokeTimeline(new[] { a, b, c }, uniformAirMove: Ms(10));

        // 60 ms draw + 2 gaps * 10 ms = 80 ms.
        Assert.Equal(Ms(80), timeline.TotalDuration);
    }

    [Fact]
    public void WrongAirMoveCountThrows()
    {
        var a = Stroke3(Guid.NewGuid(), 0);
        var b = Stroke3(Guid.NewGuid(), 100);

        // Two strokes need exactly one gap; supplying two is a contract violation.
        Assert.Throws<ArgumentException>(() => new StrokeTimeline(new[] { a, b }, new[] { Ms(5), Ms(5) }));
    }

    [Fact]
    public void NegativeAirMoveThrows()
    {
        var a = Stroke3(Guid.NewGuid(), 0);
        var b = Stroke3(Guid.NewGuid(), 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeTimeline(new[] { a, b }, new[] { Ms(-1) }));
    }
}
