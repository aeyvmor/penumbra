using Penumbra.Ink;

namespace Penumbra.App;

/// <summary>
/// The answer layer the view-model hands to the canvas: the synthesized handwriting to play, plus a
/// monotonically-increasing <see cref="Sequence"/> so the control reliably notices a replacement even when
/// two consecutive answers would otherwise compare equal. This is deliberately NOT part of the
/// <c>InkDocument</c> — the document is the recognizer's input, and folding the answer back into it would
/// make the next Recognize read the answer as if the user had written it.
/// </summary>
/// <param name="Handwriting">World-coordinate strokes and the Seam-4 timeline to replay.</param>
/// <param name="Sequence">Strictly increasing per answer; forces a styled-property change on replacement.</param>
public sealed record AnswerAnimation(SynthesizedHandwriting Handwriting, long Sequence);
