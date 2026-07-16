using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Recognition;

/// <summary>
/// The LaTeX and stroke-token alignment returned by recognition. <c>Confidence</c> is the mean
/// per-symbol score; <c>MinConfidence</c> is the weakest symbol's score (0 for an empty result) and
/// drives the reject gate — one shaky glyph should stop us acting on the whole line, and the mean hides it.
/// <c>ParseOutcome</c> (Phase 5.5 slice 4) is the spatial grammar's typed accepted/refused/ambiguous
/// verdict for this line. It is nullable and defaults to null so every pre-existing construction site
/// (test fakes, <see cref="NoOpRecognizer"/>, the empty-region path) keeps compiling with no opinion —
/// <see cref="RecognitionGate"/> treats a null outcome as "no structural opinion" and falls back to the
/// confidence/OOD gate alone. When non-null and <see cref="ParseOutcomeKind.Accepted"/>, <c>Latex</c> is
/// the tree-serialized string; otherwise <c>Latex</c> keeps the flat token assembly for display/debug and
/// must never be evaluated by the CAS.
/// </summary>
public sealed record RecognitionResult(
    string Latex,
    IReadOnlyList<RecognizedToken> Tokens,
    double Confidence,
    double MinConfidence,
    LayoutParseOutcome? ParseOutcome = null);
