using Penumbra.Graphing;

namespace Penumbra.Graphing.Tests;

public sealed class NoOpGraphDetectorTests
{
    [Fact]
    public void TryDetectReturnsNull()
    {
        var detector = new NoOpGraphDetector();

        var candidate = detector.TryDetect("y=x");

        Assert.Null(candidate);
    }
}
