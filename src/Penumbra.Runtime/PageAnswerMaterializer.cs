using Penumbra.Cas;
using Penumbra.Cas.Latex;
using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.Runtime;

/// <summary>
/// Headless answer-presentation policy shared by the interactive App and scenario runtime. It decides
/// whether a Sheet result is useful, chooses the normal answer anchor, and synthesizes the handwriting
/// that may later be stamped. Returned ink remains presentation-only until <see cref="PageStampTransaction"/>
/// explicitly inserts a transformed copy into an <see cref="InkDocument"/>.
/// </summary>
public static class PageAnswerMaterializer
{
    /// <summary>True only for a computed, non-conflicting query whose result adds useful information.</summary>
    public static bool IsAnswerableQuery(SheetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Result is { } result && IsUsefulQueryResult(node, result);
    }

    /// <summary>
    /// Rejects unresolved symbolic identities while retaining numeric, boolean, solution, and genuinely
    /// simplified symbolic results.
    /// </summary>
    public static bool IsUsefulQueryResult(SheetNode node, EvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(result);
        if (node.Role != NodeRole.Query || node.IsConflict || !result.IsComputed)
        {
            return false;
        }

        if (result.Kind != EvaluationKind.Symbolic)
        {
            return true;
        }

        string queryExpression = node.Latex.TrimEnd();
        if (queryExpression.EndsWith('=')
            && !queryExpression.EndsWith("==", StringComparison.Ordinal))
        {
            queryExpression = queryExpression[..^1];
        }

        string input = LatexToAngouriMath.Translate(queryExpression);
        string output = LatexToAngouriMath.Translate(result.Latex);
        return !string.Equals(input, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Materializes one answer with a stable seed derived only from its visible text and token geometry.
    /// The deterministic seed makes corpus geometry replayable without feeding expected answers to runtime.
    /// </summary>
    public static SynthesizedHandwriting? TrySynthesize(
        string answerText,
        IReadOnlyList<RecognizedToken> tokens,
        HandwritingSynthesizer? synthesizer)
    {
        ArgumentNullException.ThrowIfNull(answerText);
        ArgumentNullException.ThrowIfNull(tokens);
        (InkBounds Anchor, double LineHeight)? spawn = FindSpawn(tokens);
        if (synthesizer is null || spawn is null)
        {
            return null;
        }

        string handwriting = HandwritingText.FromDisplayText(answerText);
        SynthesizedHandwriting? synthesized = synthesizer.Synthesize(
            handwriting,
            spawn.Value.Anchor,
            new SynthesisOptions { LineHeight = spawn.Value.LineHeight },
            new Random(StableSeed(answerText, tokens)));
        return synthesized is null || synthesized.MissingSymbols.Count > 0
            ? null
            : synthesized;
    }

    /// <summary>The last equals token anchors an answer; a line without one has no answer spawn.</summary>
    public static (InkBounds Anchor, double LineHeight)? FindSpawn(
        IReadOnlyList<RecognizedToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        int equalsIndex = -1;
        for (int index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Latex == "=")
            {
                equalsIndex = index;
            }
        }

        return equalsIndex < 0
            ? null
            : (tokens[equalsIndex].Bounds, ClampedMedianTokenHeight(tokens));
    }

    /// <summary>
    /// Representative line height: median non-equals token height, clamped to [24, 96], with 48 as the
    /// empty/default height. Stamp scaling uses this same policy.
    /// </summary>
    public static double ClampedMedianTokenHeight(IReadOnlyList<RecognizedToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        double[] heights = tokens
            .Where(token => token.Latex != "=")
            .Select(token => token.Bounds.Height)
            .Order()
            .ToArray();
        double median = heights.Length == 0
            ? 48
            : heights.Length % 2 == 1
                ? heights[heights.Length / 2]
                : (heights[heights.Length / 2 - 1] + heights[heights.Length / 2]) / 2;
        return Math.Clamp(median, 24, 96);
    }

    // string.GetHashCode and Guid ownership are deliberately excluded: both vary by process/session.
    private static int StableSeed(string answerText, IReadOnlyList<RecognizedToken> tokens)
    {
        unchecked
        {
            uint hash = 2166136261;
            Mix(answerText, ref hash);
            foreach (RecognizedToken token in tokens)
            {
                Mix(token.Latex, ref hash);
                Mix(BitConverter.DoubleToInt64Bits(token.Bounds.X), ref hash);
                Mix(BitConverter.DoubleToInt64Bits(token.Bounds.Y), ref hash);
                Mix(BitConverter.DoubleToInt64Bits(token.Bounds.Width), ref hash);
                Mix(BitConverter.DoubleToInt64Bits(token.Bounds.Height), ref hash);
            }
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    private static void Mix(string value, ref uint hash)
    {
        foreach (char character in value)
        {
            hash = (hash ^ character) * 16777619;
        }
        hash = (hash ^ 0xFFu) * 16777619;
    }

    private static void Mix(long value, ref uint hash)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                hash = (hash ^ (byte)(value >> shift)) * 16777619;
            }
        }
    }
}
