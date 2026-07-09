using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// The reject/unknown gate (Phase 3.9c): decides whether a recognition is confident enough to act on.
/// A token is <em>uncertain</em> when its confidence falls below the threshold OR the classifier's
/// energy calibration flagged it out-of-distribution (<see cref="RecognizedToken.Rejected"/>, audit B4)
/// — a closed softmax forces junk ink into <em>some</em> class, often confidently, so rejection must
/// override confidence. When any symbol is uncertain we refuse the whole line rather than compute on a
/// guess, and we name the offending symbol (1-based) so the user knows exactly what to rewrite. Kept
/// headless here so the decision is unit-tested without the UI.
/// </summary>
public static class RecognitionGate
{
    /// <summary>The outcome of gating a recognition against a confidence threshold.</summary>
    /// <param name="Accepted">True when the read is confident enough to act on.</param>
    /// <param name="SymbolPosition">1-based index of the symbol named in the refusal (0 when accepted).</param>
    /// <param name="Refusal">A polite user-facing refusal, or null when accepted.</param>
    public readonly record struct GateResult(bool Accepted, int SymbolPosition, string? Refusal);

    /// <summary>
    /// Accepts an empty result or one with no uncertain token; otherwise rejects and points at the
    /// shakiest symbol. "Shakiest" generalizes the pre-B4 rule (lowest confidence overall) to: the
    /// lowest-confidence token <em>among the uncertain ones</em> — a genuinely weak read is still named
    /// ahead of an energy-rejected token that scored high, but a lone rejected token at confidence 0.99
    /// is nameable too (its softmax score is exactly what the energy calibration says not to trust).
    /// When no token is rejected this picks the same symbol as before B4.
    /// </summary>
    public static GateResult Evaluate(RecognitionResult result, double threshold)
    {
        ArgumentNullException.ThrowIfNull(result);

        int shakiest = IndexOfShakiestUncertain(result.Tokens, threshold);
        if (shakiest < 0)
        {
            return new GateResult(Accepted: true, SymbolPosition: 0, Refusal: null);
        }

        int position = shakiest + 1;   // 1-based for the user
        return new GateResult(
            Accepted: false,
            SymbolPosition: position,
            Refusal: $"couldn't read that (symbol {position} looks ambiguous)");
    }

    /// <summary>
    /// The strokes behind every uncertain token (Phase 4.5c) — below-threshold or energy-rejected — the
    /// set the canvas renders as glitch-ink, so a reject points at the offending ink itself instead of
    /// only naming an index. Empty when the read is confident. Pure Seam-1 projection: tokens already
    /// know their strokes.
    /// </summary>
    public static IReadOnlySet<Guid> UncertainStrokeIds(RecognitionResult result, double threshold)
    {
        ArgumentNullException.ThrowIfNull(result);

        var ids = new HashSet<Guid>();
        foreach (RecognizedToken token in result.Tokens)
        {
            if (IsUncertain(token, threshold))
            {
                foreach (Guid id in token.SourceStrokeIds)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>The one uncertainty test the gate and the glitch-ink set share.</summary>
    private static bool IsUncertain(RecognizedToken token, double threshold) =>
        token.Confidence < threshold || token.Rejected;

    /// <summary>
    /// Index of the lowest-confidence uncertain token, or -1 when every token is certain (which
    /// includes the empty result — an empty page is nothing to refuse).
    /// </summary>
    private static int IndexOfShakiestUncertain(IReadOnlyList<RecognizedToken> tokens, double threshold)
    {
        int shakiest = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (IsUncertain(tokens[i], threshold)
                && (shakiest < 0 || tokens[i].Confidence < tokens[shakiest].Confidence))
            {
                shakiest = i;
            }
        }

        return shakiest;
    }
}
