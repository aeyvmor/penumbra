using Penumbra.App;

namespace Penumbra.App.Tests;

/// <summary>
/// Phase 5.3 A1: the hold-to-grab decision, proven headless. Press on an answer and hold within the slop
/// to lift it; move past the slop first and it stays a plain ink stroke. All distances are screen pixels,
/// so the gesture feels identical at any zoom.
/// </summary>
public sealed class HoldToGrabDetectorTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);
    private const double Slop = 8;

    [Fact]
    public void HoldingWithinSlop_ConvertsAfterTheHold()
    {
        var detector = new HoldToGrabDetector(100, 100, Slop, Hold);

        // A little jitter, well inside the slop, while the hold is still counting down: still a candidate.
        Assert.Equal(
            HoldToGrabDetector.GrabState.Armed,
            detector.Update(102, 101, TimeSpan.FromMilliseconds(150)));

        // The hold elapses without a slop breach → the answer is now grabbed.
        Assert.Equal(
            HoldToGrabDetector.GrabState.Converted,
            detector.Update(103, 99, Hold));
        Assert.Equal(HoldToGrabDetector.GrabState.Converted, detector.State);
    }

    [Fact]
    public void MovingPastSlopBeforeTheHold_Disarms()
    {
        var detector = new HoldToGrabDetector(100, 100, Slop, Hold);

        // Past the slop while the hold has not elapsed — the user is drawing, not grabbing.
        Assert.Equal(
            HoldToGrabDetector.GrabState.Disarmed,
            detector.Update(100 + Slop + 5, 100, TimeSpan.FromMilliseconds(50)));

        // Terminal: holding still afterwards, even long past the hold, never revives the candidate.
        Assert.Equal(
            HoldToGrabDetector.GrabState.Disarmed,
            detector.Update(100, 100, Hold * 4));
    }

    [Fact]
    public void ReleasingBeforeTheHold_NeverConverts()
    {
        var detector = new HoldToGrabDetector(100, 100, Slop, Hold);

        // Only in-slop samples arrived before the pointer came up early; the control sees a still-Armed
        // candidate at release and runs its ordinary tap/draw ladder.
        detector.Update(101, 100, TimeSpan.FromMilliseconds(100));
        detector.Update(100, 101, TimeSpan.FromMilliseconds(250));

        Assert.Equal(HoldToGrabDetector.GrabState.Armed, detector.State);
    }

    [Fact]
    public void SlopIsScreenSpace_MeasuredInScreenPixelsWithNoZoomInput()
    {
        // The detector is fed SCREEN coordinates and never a zoom factor, so the same physical pointer
        // travel decides identically at any canvas scale: just inside the slop holds, just outside breaks.
        var justInside = new HoldToGrabDetector(0, 0, Slop, Hold);
        Assert.Equal(
            HoldToGrabDetector.GrabState.Armed,
            justInside.Update(Slop - 0.5, 0, TimeSpan.FromMilliseconds(50)));

        var justOutside = new HoldToGrabDetector(0, 0, Slop, Hold);
        Assert.Equal(
            HoldToGrabDetector.GrabState.Disarmed,
            justOutside.Update(Slop + 0.5, 0, TimeSpan.FromMilliseconds(50)));
    }
}
