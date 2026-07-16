using AngouriMath;
using Penumbra.Cas.Latex;

namespace Penumbra.Cas;

/// <summary>
/// The AngouriMath-backed <see cref="IExpressionAnalyzer"/>. It reuses the same
/// <see cref="LatexToAngouriMath"/> translator as <see cref="AngouriMathEvaluator"/>, so the graph
/// classifies exactly the syntax the evaluator later evaluates.
/// </summary>
/// <remarks>
/// This keeps ADR-0007's AngouriMath freeze contained in Cas: the graph depends only on
/// <see cref="ExpressionAnalysis"/>, never on an <c>Entity</c>. Like the evaluator, it never throws —
/// a construct AngouriMath rejects (or a parse failure) yields a safe empty analysis.
/// </remarks>
public sealed class AngouriMathExpressionAnalyzer : IExpressionAnalyzer
{
    private static readonly ExpressionAnalysis Empty =
        new(DefinedSymbol: null, FreeVariables: new HashSet<string>(), IsQuery: false);

    /// <inheritdoc />
    public ExpressionAnalysis Analyze(string latex)
    {
        try
        {
            var translated = LatexToAngouriMath.Translate(latex);
            if (string.IsNullOrWhiteSpace(translated))
            {
                return Empty;
            }

            var (left, right, hasEquals) = SplitEquation(translated);
            var hasRhs = !string.IsNullOrWhiteSpace(right);

            // A bare trailing "=" (as in "2+x=") is a compute request, not an equation to solve.
            var isQuery = hasEquals && !hasRhs;

            if (hasEquals && hasRhs)
            {
                // A definition is "single variable = expression"; anything else (e.g. "2x+3=7") is an
                // equation/statement whose dependencies are the variables on both sides.
                if (MathS.FromString(left) is Entity.Variable defined)
                {
                    return new ExpressionAnalysis(defined.Name, VarsOf(right), IsQuery: false);
                }

                var both = VarsOf(left);
                both.UnionWith(VarsOf(right));
                string? solvedSymbol = both.Count == 1 ? both.Single() : null;
                return new ExpressionAnalysis(
                    DefinedSymbol: null,
                    both,
                    IsQuery: false,
                    SolvedSymbol: solvedSymbol);
            }

            // Query ("expr=") depends on the LHS variables; a bare statement ("x+1", "x+1<3") on all.
            return new ExpressionAnalysis(DefinedSymbol: null, VarsOf(isQuery ? left : translated), isQuery);
        }
        catch
        {
            // "Analysis must not throw": a rejected construct or parse failure is simply unclassifiable.
            return Empty;
        }
    }

    /// <summary>Free variables of a parsed sub-expression, as plain names. Empty when blank or unparsable.</summary>
    private static HashSet<string> VarsOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>();
        }

        // AngouriMath resolves pi / e as constants during evaluation, so they must not read as free
        // variables — the graph would otherwise invent a dependency the evaluator never honours.
        return MathS.FromString(text).Vars
            .Select(v => v.Name)
            .Where(name => name is not ("pi" or "e"))
            .ToHashSet();
    }

    /// <summary>
    /// Splits on the first top-level, standalone <c>=</c> (ignoring <c>&lt;=</c>, <c>&gt;=</c>,
    /// <c>!=</c>, <c>==</c>). Mirrors <see cref="AngouriMathEvaluator"/>'s split so the analyzer and the
    /// evaluator agree on where a definition/equation divides.
    /// </summary>
    private static (string Left, string Right, bool HasEquals) SplitEquation(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case '=' when depth == 0:
                {
                    var prev = i > 0 ? text[i - 1] : '\0';
                    var next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (prev is '<' or '>' or '!' or '=' || next == '=')
                    {
                        break; // part of a relational/equality operator, not a split point
                    }

                    return (text[..i], text[(i + 1)..], true);
                }
            }
        }

        return (text, string.Empty, false);
    }
}
