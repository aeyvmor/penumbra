namespace Penumbra.App;

/// <summary>
/// The pure, headless decision core for hold-to-grab (Phase 5.3 A1): given where a press landed and the
/// pointer samples that follow, decides whether the press has become a deliberate GRAB of an answer or is
/// still a plain ink stroke. Kept UI-free so the hold/slop timing is unit-tested without a canvas or a real
/// timer — the control glues it to a <c>DispatcherTimer</c> and pointer moves.
///
/// <para>The gesture is: press on an answer, then hold roughly still for <c>holdDuration</c>. Moving the
/// pointer past <c>slopScreenPx</c> before the hold completes means the user is drawing, not grabbing, and
/// the candidate is <see cref="GrabState.Disarmed"/> for good; staying within the slop until the hold
/// elapses <see cref="GrabState.Converted"/>s it to a drag. All distances are SCREEN pixels, so the feel is
/// identical at any zoom — the control feeds screen coordinates and the slop never scales.</para>
/// </summary>
public sealed class HoldToGrabDetector
{
    private readonly double _originX;
    private readonly double _originY;
    private readonly double _slopSquared;
    private readonly TimeSpan _hold;

    /// <summary>The lifecycle of one grab candidate; <see cref="Disarmed"/> and <see cref="Converted"/> are terminal.</summary>
    public enum GrabState
    {
        /// <summary>Still a candidate: within slop and the hold has not yet elapsed.</summary>
        Armed,

        /// <summary>The pointer left the slop before the hold completed — this is a plain ink stroke.</summary>
        Disarmed,

        /// <summary>Held within slop for the full hold — the answer is now being dragged.</summary>
        Converted,
    }

    /// <summary>
    /// Arms a candidate at the press point (screen coordinates). <paramref name="slopScreenPx"/> is the
    /// movement budget, in screen pixels, before the hold disarms; <paramref name="holdDuration"/> is how
    /// long the pointer must stay within it to convert.
    /// </summary>
    public HoldToGrabDetector(double pressX, double pressY, double slopScreenPx, TimeSpan holdDuration)
    {
        if (slopScreenPx < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slopScreenPx), slopScreenPx, "slop must be non-negative");
        }

        if (holdDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdDuration), holdDuration, "hold duration must be positive");
        }

        _originX = pressX;
        _originY = pressY;
        _slopSquared = slopScreenPx * slopScreenPx;
        _hold = holdDuration;
    }

    /// <summary>The current state; once it leaves <see cref="GrabState.Armed"/> it never changes again.</summary>
    public GrabState State { get; private set; } = GrabState.Armed;

    /// <summary>
    /// Feeds the latest pointer sample (screen coordinates) and the time elapsed since the press, returning
    /// the resulting <see cref="State"/>. A slop breach wins over an elapsed hold within the same update — a
    /// pointer that has already left the slop is drawing, no matter how much time passed. Calls made after
    /// the candidate is terminal are ignored and return the settled state.
    /// </summary>
    public GrabState Update(double x, double y, TimeSpan elapsed)
    {
        if (State != GrabState.Armed)
        {
            return State;
        }

        double dx = x - _originX;
        double dy = y - _originY;
        if (dx * dx + dy * dy > _slopSquared)
        {
            return State = GrabState.Disarmed;
        }

        if (elapsed >= _hold)
        {
            return State = GrabState.Converted;
        }

        return State;
    }
}
