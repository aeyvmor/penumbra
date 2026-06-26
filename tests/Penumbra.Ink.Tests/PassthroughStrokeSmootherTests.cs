using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class PassthroughStrokeSmootherTests
{
    [Fact]
    public void SmoothReturnsInputStroke()
    {
        var stroke = new Stroke(Guid.NewGuid(), Array.Empty<StrokeSample>());
        var smoother = new PassthroughStrokeSmoother();

        var smoothed = smoother.Smooth(stroke);

        Assert.Same(stroke, smoothed);
    }
}
