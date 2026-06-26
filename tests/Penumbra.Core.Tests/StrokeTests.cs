using Penumbra.Core;

namespace Penumbra.Core.Tests;

public sealed class StrokeTests
{
    [Fact]
    public void StrokePreservesOnlineSamples()
    {
        var sample = new StrokeSample(12, 34, TimeSpan.FromMilliseconds(5), 0.75);
        var stroke = new Stroke(Guid.NewGuid(), new[] { sample });

        Assert.Equal(sample, stroke.Samples[0]);
    }
}
