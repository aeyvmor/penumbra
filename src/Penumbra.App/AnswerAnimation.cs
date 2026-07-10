using Penumbra.Ink;

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
