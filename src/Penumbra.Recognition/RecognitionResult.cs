using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// The LaTeX and stroke-token alignment returned by recognition. <c>Confidence</c> is the mean
/// per-symbol score; <c>MinConfidence</c> is the weakest symbol's score (0 for an empty result) and
/// drives the reject gate — one shaky glyph should stop us acting on the whole line, and the mean hides it.
/// </summary>
public sealed record RecognitionResult(
    string Latex,
    IReadOnlyList<RecognizedToken> Tokens,
    double Confidence,
    double MinConfidence);
