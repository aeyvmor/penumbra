using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class SmoothedStrokeCacheTests
{
    // A smoother that returns a distinct, recognizable output and counts how many times it ran, so a
    // test can tell "smoothed" from "raw" and prove the cache computes each id at most once.
    private sealed class CountingSmoother : IStrokeSmoother
    {
        public int Calls { get; private set; }

        public Stroke Smooth(Stroke stroke)
        {
            Calls++;
            return stroke with { Samples = new[] { new StrokeSample(-1, -1, TimeSpan.Zero, 0) } };
        }
    }

    private static Stroke Raw(Guid id) => new(id, new[]
    {
        new StrokeSample(0, 0, TimeSpan.Zero, 0.5),
        new StrokeSample(1, 1, TimeSpan.FromMilliseconds(5), 0.5),
    });

    [Fact]
    public void GetSmoothed_ReturnsSmootherOutput()
    {
        var smoother = new CountingSmoother();
        var cache = new SmoothedStrokeCache(smoother);
        Stroke raw = Raw(Guid.NewGuid());

        Stroke result = cache.GetSmoothed(raw);

        // The cache hands back what the smoother produced, not the raw input.
        Assert.NotEqual(raw.Samples, result.Samples);
        Assert.Equal(new[] { new StrokeSample(-1, -1, TimeSpan.Zero, 0) }, result.Samples);
        Assert.Equal(raw.Id, result.Id);
    }

    [Fact]
    public void GetSmoothed_SecondCallSameId_UsesCache()
    {
        var smoother = new CountingSmoother();
        var cache = new SmoothedStrokeCache(smoother);
        Stroke raw = Raw(Guid.NewGuid());

        Stroke first = cache.GetSmoothed(raw);
        Stroke second = cache.GetSmoothed(raw);

        Assert.Same(first, second);
        Assert.Equal(1, smoother.Calls);
    }

    [Fact]
    public void GetSmoothed_DifferentIds_ComputedSeparately()
    {
        var smoother = new CountingSmoother();
        var cache = new SmoothedStrokeCache(smoother);

        cache.GetSmoothed(Raw(Guid.NewGuid()));
        cache.GetSmoothed(Raw(Guid.NewGuid()));

        Assert.Equal(2, smoother.Calls);
    }

    [Fact]
    public void EvictMissing_DropsAbsentIds_RecomputesAfterwards()
    {
        var smoother = new CountingSmoother();
        var cache = new SmoothedStrokeCache(smoother);
        Stroke raw = Raw(Guid.NewGuid());

        cache.GetSmoothed(raw);
        cache.EvictMissing(Array.Empty<Guid>()); // stroke removed (clear/undo)
        cache.GetSmoothed(raw);                   // reappears (redo) -> recomputed

        Assert.Equal(2, smoother.Calls);
    }

    [Fact]
    public void EvictMissing_KeepsLiveIds()
    {
        var smoother = new CountingSmoother();
        var cache = new SmoothedStrokeCache(smoother);
        Stroke raw = Raw(Guid.NewGuid());

        cache.GetSmoothed(raw);
        cache.EvictMissing(new[] { raw.Id });
        cache.GetSmoothed(raw);

        Assert.Equal(1, smoother.Calls);
    }
}
