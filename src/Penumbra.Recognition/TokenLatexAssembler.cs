using System.Text;

namespace Penumbra.Recognition;

/// <summary>
/// Assembles ordered token labels into the linear-grammar LaTeX string the recognizer emits.
/// Extracted from <see cref="ExpressionRecognizer"/> so the identical rule can be reused when a
/// taffy drag splices a literal run back into a line (see <see cref="LiteralRuns.Splice"/>): the
/// spliced probe must read byte-for-byte the way the recognizer would have assembled it, or a
/// re-evaluated trial value would diverge from the live line.
/// </summary>
public static class TokenLatexAssembler
{
    /// <summary>
    /// Concatenates <paramref name="labels"/> left-to-right into LaTeX and trims a trailing
    /// separator. Pure — same input always yields the same output, with no state.
    /// <para>
    /// 3.9a: a control word ("\pi", "\times", …) gets a single trailing space, else "\pi" followed
    /// by "x" assembles to "\pix" — a phantom variable the translator reads as "pix". Digits and
    /// letters stay directly concatenated (multi-digit numbers depend on it: "2""1" must be 21, not
    /// a spaced "2 1" the translator would turn into 2*1). A line-final control word's separator is
    /// trimmed off by the closing <c>TrimEnd()</c>.
    /// </para>
    /// </summary>
    public static string Assemble(IReadOnlyList<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var latex = new StringBuilder();
        foreach (string label in labels)
        {
            latex.Append(label);
            if (label.StartsWith('\\'))
            {
                latex.Append(' ');
            }
        }

        return latex.ToString().TrimEnd();
    }
}
