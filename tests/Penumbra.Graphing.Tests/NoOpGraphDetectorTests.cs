using static Penumbra.Graphing.Tests.LayoutTreeFactory;

namespace Penumbra.Graphing.Tests;

public sealed class NoOpGraphDetectorTests
{
    private static readonly NoOpGraphDetector Detector = new();

    [Fact]
    public void Detect_String_AlwaysRejects()
    {
        var outcome = Detector.Detect("y=x");

        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.Candidate);
    }

    [Fact]
    public void Detect_Tree_AlwaysRejects()
    {
        var outcome = Detector.Detect(Eq(Leaf("y"), Leaf("x")));

        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.Candidate);
    }

    [Fact]
    public void Detect_String_ThrowsOnNull() =>
        Assert.Throws<ArgumentNullException>(() => Detector.Detect((string)null!));

    [Fact]
    public void Detect_Tree_ThrowsOnNull() =>
        Assert.Throws<ArgumentNullException>(() => Detector.Detect((Penumbra.Core.Layout.LayoutNode)null!));
}
