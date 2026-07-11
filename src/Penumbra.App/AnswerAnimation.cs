using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.App;

/// <summary>
/// The answer layer the view-model hands to the canvas: the synthesized handwriting to play, plus a
/// monotonically-increasing <see cref="Sequence"/> so the control reliably notices a replacement even when
/// two consecutive answers would otherwise compare equal. This is deliberately NOT part of the
/// <c>InkDocument</c> — the document is the recognizer's input, and folding the answer back into it would
/// make the next Recognize read the answer as if the user had written it.
/// </summary>
/// <param name="OwnerId">Stable Sheet node/recognition-region owner.</param>
/// <param name="Handwriting">World-coordinate strokes and the Seam-4 timeline to replay.</param>
/// <param name="Sequence">Strictly increasing per answer; forces a styled-property change on replacement.</param>
/// <param name="Play">False when load should show the final frame without replay.</param>
public sealed record AnswerAnimation(
    Guid OwnerId,
    SynthesizedHandwriting Handwriting,
    long Sequence,
    bool Play = true);

/// <summary>
/// An immutable snapshot of all synthesized answers. Ownership is the Sheet node/recognition-region id;
/// this layer is presentation-only and must never be inserted into <c>InkDocument</c> or recognition.
/// </summary>
public sealed record AnswerLayer(IReadOnlyList<AnswerAnimation> Answers)
{
    public static AnswerLayer Empty { get; } = new(Array.Empty<AnswerAnimation>());
}

/// <summary>One dependency-order step in the transient causality glow.</summary>
public sealed record CausalityRippleStep(Guid OwnerId, IReadOnlyList<Guid> StrokeIds);

/// <summary>
/// A transient dependency effect, separate from answer playback and persistent provenance selection.
/// Sequence forces a new visual even when two edits happen to affect the same ordered nodes.
/// </summary>
public sealed record CausalityRipple(IReadOnlyList<CausalityRippleStep> Steps, long Sequence);

/// <summary>Owner-aware answer tap payload used to recover the correct Seam-1 provenance.</summary>
public sealed class AnswerTappedEventArgs(Guid ownerId) : EventArgs
{
    public Guid OwnerId { get; } = ownerId;
}

/// <summary>
/// Payload for a held-and-dragged answer being dropped to stamp into the document as real ink (Phase 5.3
/// A1). Carries the world-space drag delta so the stamp lands exactly where the drag ghost showed, and the
/// world-space drop point so the view-model can tell which existing line — if any — the answer landed on.
/// </summary>
/// <param name="ownerId">The answer owner (Sheet node / recognition-region id) being stamped.</param>
/// <param name="worldDx">World-space horizontal distance from the grab point to the drop point.</param>
/// <param name="worldDy">World-space vertical distance from the grab point to the drop point.</param>
/// <param name="worldDropX">World-space X of the drop point.</param>
/// <param name="worldDropY">World-space Y of the drop point.</param>
public sealed class AnswerDragCompletedEventArgs(
    Guid ownerId,
    double worldDx,
    double worldDy,
    double worldDropX,
    double worldDropY) : EventArgs
{
    public Guid OwnerId { get; } = ownerId;
    public double WorldDx { get; } = worldDx;
    public double WorldDy { get; } = worldDy;
    public double WorldDropX { get; } = worldDropX;
    public double WorldDropY { get; } = worldDropY;
}

/// <summary>One accepted line's grabbable numeric literals (Phase 5.3 interaction layer).</summary>
/// <param name="OwnerId">The recognition-region / Sheet node the runs belong to.</param>
/// <param name="Runs">The literal runs <see cref="LiteralRuns.Find"/> reported for that line's tokens.</param>
public sealed record LiteralRunOwner(Guid OwnerId, IReadOnlyList<LiteralRun> Runs);

/// <summary>
/// An immutable snapshot of every accepted line's numeric literals — the data a "taffy" drag will grab
/// (Phase 5.3). Published by the view-model, bound to the canvas, and validated again when a held literal
/// begins taffy. <see cref="Sequence"/> forces a styled-property change even
/// when two snapshots would otherwise compare equal, exactly as <see cref="AnswerAnimation.Sequence"/> does.
/// </summary>
public sealed record LiteralRunLayer(IReadOnlyList<LiteralRunOwner> Owners, long Sequence)
{
    public static LiteralRunLayer Empty { get; } = new(Array.Empty<LiteralRunOwner>(), 0);

    /// <summary>Total run count across all owners — a cheap way to notice a prune actually changed something.</summary>
    public int RunCount => Owners.Sum(owner => owner.Runs.Count);

    /// <summary>
    /// Drops any run whose source strokes are no longer all present in the document — an edited or erased
    /// line must not stay grabbable at a stale value. Returns a layer carrying only the still-valid runs
    /// (owners left with no runs drop out); <paramref name="sequence"/> stamps the pruned snapshot.
    /// </summary>
    public LiteralRunLayer PruneMissing(IReadOnlySet<Guid> presentStrokeIds, long sequence)
    {
        ArgumentNullException.ThrowIfNull(presentStrokeIds);

        var owners = new List<LiteralRunOwner>(Owners.Count);
        foreach (LiteralRunOwner owner in Owners)
        {
            LiteralRun[] kept = owner.Runs
                .Where(run => run.SourceStrokeIds.All(presentStrokeIds.Contains))
                .ToArray();
            if (kept.Length > 0)
            {
                owners.Add(owner with { Runs = kept });
            }
        }

        return new LiteralRunLayer(owners, sequence);
    }
}

/// <summary>
/// One static piece of hypothetical handwriting shown during a taffy gesture. Literal ghosts float
/// above the grabbed source run; answer ghosts occupy the normal answer anchor for their query owner.
/// They are presentation-only and never enter the document, animation timeline, or recognition cache.
/// </summary>
/// <param name="OwnerId">The Sheet node whose literal or trial result this ghost represents.</param>
/// <param name="ValueText">The exact trial display value used to synthesize the ghost.</param>
/// <param name="Handwriting">Final-frame handwriting; its timeline is deliberately never replayed.</param>
/// <param name="IsLiteral">True for the grabbed literal itself; false for a downstream trial answer.</param>
/// <param name="LiftScreenPx">Vertical lift applied by the canvas in screen pixels, keeping it zoom-invariant.</param>
public sealed record TaffyGhost(
    Guid OwnerId,
    string ValueText,
    SynthesizedHandwriting Handwriting,
    bool IsLiteral,
    double LiftScreenPx = 0);

/// <summary>
/// Immutable render snapshot for one active taffy session. Source literal strokes are muted, committed
/// answers whose hypothetical value is being shown are hidden, and <see cref="Ghosts"/> supplies their
/// static replacements. Absence of a layer means no active taffy session.
/// </summary>
/// <param name="MutedStrokeIds">The grabbed literal's source strokes, rendered dimly in place.</param>
/// <param name="HiddenAnswerOwnerIds">Committed answer owners suppressed while trial ghosts are visible.</param>
/// <param name="Ghosts">Static literal and answer ghosts for the current snapped value.</param>
/// <param name="Sequence">Monotonic visual version, ensuring every accepted trial invalidates the canvas.</param>
public sealed record TaffyGhostLayer(
    IReadOnlySet<Guid> MutedStrokeIds,
    IReadOnlySet<Guid> HiddenAnswerOwnerIds,
    IReadOnlyList<TaffyGhost> Ghosts,
    long Sequence);

/// <summary>Payload for a held literal asking the ViewModel to begin a validated taffy session.</summary>
public sealed class TaffyStartedEventArgs(Guid ownerId, LiteralRun run) : EventArgs
{
    public Guid OwnerId { get; } = ownerId;
    public LiteralRun Run { get; } = run;

    /// <summary>
    /// Set synchronously by the host after validating that the run still belongs to the current document.
    /// A rejected stale snapshot is consumed safely and restarts the recognition cancelled by pen-down.
    /// </summary>
    public bool Accepted { get; set; }
}

/// <summary>Screen-space cumulative horizontal motion for an active taffy gesture.</summary>
public sealed class TaffyMovedEventArgs(double screenDx) : EventArgs
{
    public double ScreenDx { get; } = screenDx;
}
