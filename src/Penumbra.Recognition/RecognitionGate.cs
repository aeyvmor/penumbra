using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// The reject/unknown gate (Phase 3.9c): decides whether a recognition is confident enough to act on.
/// When the weakest symbol falls below the threshold we refuse the whole line rather than compute on a
/// guess, and we name the offending symbol (1-based) so the user knows exactly what to rewrite. Kept
/// headless here so the decision is unit-tested without the UI.
/// </summary>
public static class RecognitionGate
{
    /// <summary>The outcome of gating a recognition against a confidence threshold.</summary>
    /// <param name="Accepted">True when the read is confident enough to act on.</param>
    /// <param name="SymbolPosition">1-based index of the least-confident symbol (0 when accepted).</param>
    /// <param name="Refusal">A polite user-facing refusal, or null when accepted.</param>
    public readonly record struct GateResult(bool Accepted, int SymbolPosition, string? Refusal);

    /// <summary>
    /// Accepts an empty result or one whose minimum symbol confidence meets <paramref name="threshold"/>;
    /// otherwise rejects and points at the shakiest symbol.
    /// </summary>
    public static GateResult Evaluate(RecognitionResult result, double threshold)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Tokens.Count == 0 || result.MinConfidence >= threshold)
        {
            return new GateResult(Accepted: true, SymbolPosition: 0, Refusal: null);
        }

        int position = IndexOfMinConfidence(result.Tokens) + 1;   // 1-based for the user
        return new GateResult(
            Accepted: false,
            SymbolPosition: position,
            Refusal: $"couldn't read that (symbol {position} looks ambiguous)");
    }

    private static int IndexOfMinConfidence(IReadOnlyList<RecognizedToken> tokens)
    {
        int minIndex = 0;
        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Confidence < tokens[minIndex].Confidence)
            {
                minIndex = i;
            }
        }

        return minIndex;
    }
}
