using System.Text;
using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// A maximal run of consecutive recognized tokens that spell one numeric literal — the grabbable
/// unit for a "taffy" drag (grab a number, drag horizontally to scrub its value). Carries what the
/// UI needs: where the run sits in the token list (<c>TokenStart</c> / <c>TokenCount</c>), the
/// literal it currently reads (<c>ValueText</c>), the combined ink box to hit-test against
/// (<c>UnionBounds</c>), and the source strokes that drew it (<c>SourceStrokeIds</c>, in token
/// order) so the drag can highlight or re-ink them.
/// </summary>
public sealed record LiteralRun(
    int TokenStart,
    int TokenCount,
    string ValueText,
    InkBounds UnionBounds,
    IReadOnlyList<Guid> SourceStrokeIds);

/// <summary>
/// Finds the numeric literals in a recognized line and splices a scrubbed trial value back into it.
/// Pure and headless: the App layer owns the drag gesture and hit-testing; this owns only the
/// token-level "which tokens are a grabbable number" and "rebuild the LaTeX with this number
/// replaced" logic, so both are unit-testable without any UI.
/// </summary>
public static class LiteralRuns
{
    /// <summary>
    /// Returns the maximal runs of consecutive tokens whose <see cref="RecognizedToken.Latex"/> is a
    /// single ASCII digit or <c>"."</c>, left-to-right. Runs bounded by anything else (operators,
    /// <c>=</c>, control words, letters) split into separate literals. A run that is a lone <c>"."</c>
    /// (maps to no number) or contains more than one <c>"."</c> (not a valid literal) is dropped, since
    /// neither is a value a taffy drag could scrub. Never returns null; empty when there is no literal.
    /// </summary>
    public static IReadOnlyList<LiteralRun> Find(IReadOnlyList<RecognizedToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var runs = new List<LiteralRun>();
        int i = 0;
        while (i < tokens.Count)
        {
            if (!IsLiteralChar(tokens[i].Latex))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < tokens.Count && IsLiteralChar(tokens[i].Latex))
            {
                i++;
            }

            LiteralRun? run = BuildRun(tokens, start, i - start);
            if (run is not null)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    /// <summary>
    /// Rebuilds the line's LaTeX with <paramref name="run"/>'s tokens replaced by
    /// <paramref name="valueText"/>, assembling via <see cref="TokenLatexAssembler"/> so the result
    /// matches how the recognizer would have read the same tokens. A negative
    /// <paramref name="valueText"/> (leading <c>'-'</c>) is parenthesized — bare <c>"2+-3"</c> is
    /// unproven in the LaTeX translator, but <c>"2+(-3)"</c> is a group it already handles.
    /// </summary>
    public static string Splice(IReadOnlyList<RecognizedToken> tokens, LiteralRun run, string valueText)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(valueText);

        string replacement = valueText.StartsWith('-') ? "(" + valueText + ")" : valueText;

        var labels = new List<string>(tokens.Count - run.TokenCount + 1);
        for (int i = 0; i < run.TokenStart; i++)
        {
            labels.Add(tokens[i].Latex);
        }

        labels.Add(replacement);

        for (int i = run.TokenStart + run.TokenCount; i < tokens.Count; i++)
        {
            labels.Add(tokens[i].Latex);
        }

        return TokenLatexAssembler.Assemble(labels);
    }

    private static LiteralRun? BuildRun(IReadOnlyList<RecognizedToken> tokens, int start, int count)
    {
        var value = new StringBuilder();
        var strokeIds = new List<Guid>();
        int dotCount = 0;
        InkBounds union = tokens[start].Bounds;

        for (int j = start; j < start + count; j++)
        {
            RecognizedToken token = tokens[j];
            value.Append(token.Latex);
            strokeIds.AddRange(token.SourceStrokeIds);
            if (token.Latex == ".")
            {
                dotCount++;
            }

            if (j != start)
            {
                union = Union(union, token.Bounds);
            }
        }

        string valueText = value.ToString();

        // A lone "." maps to no number and ">1 dot" is not a valid literal — neither is scrubbable.
        if (dotCount > 1 || valueText == ".")
        {
            return null;
        }

        return new LiteralRun(start, count, valueText, union, strokeIds);
    }

    private static bool IsLiteralChar(string latex) =>
        latex.Length == 1 && (char.IsAsciiDigit(latex[0]) || latex[0] == '.');

    // Smallest axis-aligned box covering both, in canvas coordinates.
    private static InkBounds Union(InkBounds a, InkBounds b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double right = Math.Max(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new InkBounds(x, y, right - x, bottom - y);
    }
}
