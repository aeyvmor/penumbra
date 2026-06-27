using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class ChaikinStrokeSmootherTests
{
    // A right-angle corner: the bend at (10,0) is what corner-cutting should round off.
    private static Stroke Corner() => new(Guid.NewGuid(), new[]
    {
        new StrokeSample(0, 0, TimeSpan.FromMilliseconds(0), 0.3),
        new StrokeSample(10, 0, TimeSpan.FromMilliseconds(10), 0.6),
        new StrokeSample(10, 10, TimeSpan.FromMilliseconds(20), 0.9),
    });

    [Fact]
    public void PreservesEndpoints()
    {
        Stroke source = Corner();

        Stroke result = new ChaikinStrokeSmoother(iterations: 2).Smooth(source);

        Assert.Equal(source.Samples[0], result.Samples[0]);
        Assert.Equal(source.Samples[^1], result.Samples[^1]);
    }

    [Fact]
    public void AddsPointsByCuttingCorners()
    {
        Stroke source = Corner();

        Stroke result = new ChaikinStrokeSmoother(iterations: 1).Smooth(source);

        Assert.True(result.Samples.Count > source.Samples.Count);
        // The original sharp vertex should no longer appear verbatim.
        Assert.DoesNotContain(new StrokeSample(10, 0, TimeSpan.FromMilliseconds(10), 0.6), result.Samples);
    }

    [Fact]
    public void KeepsTimeMonotonic()
    {
        Stroke result = new ChaikinStrokeSmoother(iterations: 3).Smooth(Corner());

        for (int i = 1; i < result.Samples.Count; i++)
        {
            Assert.True(result.Samples[i].Time >= result.Samples[i - 1].Time);
        }
    }

    [Fact]
    public void ZeroIterationsIsNoOp()
    {
        Stroke source = Corner();

        Stroke result = new ChaikinStrokeSmoother(iterations: 0).Smooth(source);

        Assert.Same(source.Samples, result.Samples);
    }

    [Fact]
    public void LeavesTwoPointStrokeUnchanged()
    {
        var line = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
            new StrokeSample(5, 5, TimeSpan.FromMilliseconds(5), 0.5),
        });

        Stroke result = new ChaikinStrokeSmoother().Smooth(line);

        Assert.Same(line.Samples, result.Samples);
    }

    [Fact]
    public void RejectsNegativeIterations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChaikinStrokeSmoother(iterations: -1));
    }
}
