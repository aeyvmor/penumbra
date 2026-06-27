using AngouriMath;
using Penumbra.Cas.Latex;

namespace Penumbra.Cas;

/// <summary>
/// The Phase 2 CAS: translates LaTeX, then evaluates / simplifies / solves via AngouriMath.
/// </summary>
/// <remarks>
/// This is the concrete <see cref="IEvaluator"/> (SEAM 3). It owns three behaviours:
/// <list type="bullet">
///   <item>an <c>expr</c> (optionally ending in <c>=</c>) is simplified / evaluated;</item>
///   <item>an <c>lhs = rhs</c> equation with one unknown is solved for that unknown;</item>
///   <item>flat variable bindings from the request are substituted before evaluating.</item>
/// </list>
/// It never touches the network and never throws to callers — failures come back as an
/// <see cref="EvaluationKind.Error"/> result.
/// </remarks>
public sealed class AngouriMathEvaluator : IEvaluator
{
    /// <inheritdoc />
    public EvaluationResult Evaluate(EvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var translated = LatexToAngouriMath.Translate(request.Latex);
        if (string.IsNullOrWhiteSpace(translated))
        {
            return Error("Nothing to evaluate.");
        }

        try
        {
            var (left, right, hasEquals) = SplitEquation(translated);

            // "lhs = rhs" with a real right-hand side is an equation to solve; a bare trailing
            // "=" (as in "2+3=") is just a request to compute the left-hand side.
            if (hasEquals && !string.IsNullOrWhiteSpace(right))
            {
                return SolveEquation(left, right, request.Variables);
            }

            return EvaluateExpression(hasEquals ? left : translated, request.Variables);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static EvaluationResult EvaluateExpression(
        string text,
        IReadOnlyDictionary<string, string> variables)
    {
        var expr = ApplyBindings(MathS.FromString(text), variables);
        var simplified = expr.Simplify();

        if (simplified.EvaluableNumerical)
        {
            // Keep the exact simplified form (1/3, sqrt(2), 4) rather than a lossy decimal.
            return new EvaluationResult(
                simplified.Latexise(),
                simplified.Stringize(),
                IsComputed: true,
                EvaluationKind.Number);
        }

        return new EvaluationResult(
            simplified.Latexise(),
            simplified.Stringize(),
            IsComputed: true,
            EvaluationKind.Symbolic);
    }

    private static EvaluationResult SolveEquation(
        string left,
        string right,
        IReadOnlyDictionary<string, string> variables)
    {
        var lhs = ApplyBindings(MathS.FromString(left), variables);
        var rhs = ApplyBindings(MathS.FromString(right), variables);
        var difference = lhs - rhs;

        var unknowns = difference.Vars.ToList();
        if (unknowns.Count == 0)
        {
            // No unknowns left → it's an assertion like "2 + 2 = 4": report its truth.
            var holds = difference.EvalNumerical().IsZero;
            return new EvaluationResult(
                holds ? @"\top" : @"\bot",
                holds ? "True" : "False",
                IsComputed: true,
                EvaluationKind.Boolean);
        }

        // Flat version: solve for the single unknown (the first, if somehow several appear).
        var target = unknowns[0];
        var solutions = difference.SolveEquation(target);

        if (solutions is Entity.Set.FiniteSet finite)
        {
            var roots = finite.Select(root => root.Simplify()).ToList();
            if (roots.Count == 0)
            {
                return new EvaluationResult(string.Empty, "No solution", IsComputed: true, EvaluationKind.Solution);
            }

            var display = string.Join(" or ", roots.Select(root => $"{target} = {root.Stringize()}"));
            var latex = string.Join(@",\quad ", roots.Select(root => $"{target.Latexise()} = {root.Latexise()}"));
            return new EvaluationResult(latex, display, IsComputed: true, EvaluationKind.Solution);
        }

        // Non-finite solution set (e.g. infinitely many): hand back its symbolic form.
        return new EvaluationResult(
            solutions.Latexise(),
            solutions.Stringize(),
            IsComputed: true,
            EvaluationKind.Solution);
    }

    private static Entity ApplyBindings(Entity expression, IReadOnlyDictionary<string, string> variables)
    {
        if (variables is null)
        {
            return expression;
        }

        foreach (var (name, rawValue) in variables)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = MathS.FromString(LatexToAngouriMath.Translate(rawValue));
            expression = expression.Substitute(MathS.Var(name), value);
        }

        return expression;
    }

    /// <summary>Splits on the first top-level, standalone <c>=</c> (ignoring <c>&lt;=</c>, <c>&gt;=</c>, <c>!=</c>).</summary>
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

    private static EvaluationResult Error(string message) =>
        new(string.Empty, message, IsComputed: false, EvaluationKind.Error);
}
