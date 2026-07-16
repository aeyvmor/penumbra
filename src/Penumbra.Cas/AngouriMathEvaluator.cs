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

        try
        {
            // Translation runs inside the guard so a rejected construct (e.g. \pm throwing
            // NotSupportedException) surfaces as a graceful Error, honouring "never throws to callers".
            var translated = LatexToAngouriMath.Translate(request.Latex);
            if (string.IsNullOrWhiteSpace(translated))
            {
                return Error("Nothing to evaluate.");
            }

            var (left, right, hasEquals) = SplitEquation(translated);

            // "lhs = rhs" with a real right-hand side is an equation to solve; a bare trailing
            // "=" (as in "2+3=") is just a request to compute the left-hand side.
            if (hasEquals && !string.IsNullOrWhiteSpace(right))
            {
                // More than one top-level "=" (e.g. "x^2-5x+6=0=" or "2x+3y=6=") is not a solvable
                // equation. Refuse cleanly here instead of feeding "0=" to AngouriMath, whose parser
                // throws a multi-line ANTLR dump that would otherwise leak into the result text.
                if (SplitEquation(right).HasEquals)
                {
                    return Error("An expression can contain at most one '=' relation.");
                }

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

        if (simplified.EvaluableBoolean)
        {
            var holds = (bool)simplified.EvalBoolean();
            return new EvaluationResult(
                holds ? @"\top" : @"\bot",
                holds ? "True" : "False",
                IsComputed: true,
                EvaluationKind.Boolean);
        }

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

        // SOLVE-TARGET POLICY (Slice 6): an equation with more than one unknown has no single honest
        // target. Silently solving for the first (AngouriMath's default) answers a *different* question
        // than the one asked — "2x+3y=6" is not "x = 3 - 3y/2" — so refuse with a typed Error rather
        // than emit a plausible wrong solution. Bindings are applied above, so a peer-supplied value
        // that reduces the equation to a single unknown (e.g. "x+y=10" with y=3) still solves.
        if (unknowns.Count > 1)
        {
            return Error("This equation has more than one unknown; a single solve target is required.");
        }

        var target = unknowns[0];

        Entity solutions;
        try
        {
            solutions = difference.SolveEquation(target);
        }
        catch (Exception)
        {
            // AngouriMath can throw mid-solve on some rational forms (e.g. "(x^2-1)/(x-1)=0" yields an
            // uncompilable "Providedf" node). Surface a clean typed refusal, never the raw engine text.
            return Error("This equation could not be solved safely.");
        }

        if (solutions is Entity.Set.FiniteSet finite)
        {
            var validated = new List<Entity>();
            foreach (var candidate in finite)
            {
                var root = candidate.Simplify();

                // Reject parametric / infinite solution families: abs(x)=3 solves to 3*e^(i*r_1) and
                // sin(x)=0 to 2*n_1*pi — each introduces a free parameter the target never had. These
                // cannot be shown as a finite school answer, so the whole class refuses honestly rather
                // than printing confident garbage.
                if (root.Vars.Any())
                {
                    return Error("This equation has infinitely many or parametric solutions.");
                }

                switch (ValidateRoot(difference, target, root))
                {
                    case RootStatus.Valid:
                        validated.Add(root);
                        break;
                    case RootStatus.Extraneous:
                        break; // Drop: substituting it into the ORIGINAL equation does not hold.
                    default:
                        // A root we cannot verify is never returned unchecked — the class refuses.
                        return Error("This equation's solutions could not be verified.");
                }
            }

            // Every candidate was extraneous (e.g. sqrt(x)=-2) → report no solution honestly, rather
            // than a root that fails the original equation.
            if (validated.Count == 0)
            {
                return new EvaluationResult(string.Empty, "No solution", IsComputed: true, EvaluationKind.Solution);
            }

            var display = string.Join(" or ", validated.Select(root => $"{target} = {root.Stringize()}"));
            var latex = string.Join(@",\quad ", validated.Select(root => $"{target.Latexise()} = {root.Latexise()}"));
            return new EvaluationResult(latex, display, IsComputed: true, EvaluationKind.Solution);
        }

        // Non-finite solution set (rare; AngouriMath usually parameterizes an infinite family into a
        // FiniteSet, caught above). Hand back its symbolic form as before.
        return new EvaluationResult(
            solutions.Latexise(),
            solutions.Stringize(),
            IsComputed: true,
            EvaluationKind.Solution);
    }

    /// <summary>The verdict on a candidate root, from substituting it back into the original equation.</summary>
    private enum RootStatus
    {
        /// <summary>The root satisfies the original equation (its residual vanishes).</summary>
        Valid,

        /// <summary>The root does not satisfy the original equation (a radical/rational extraneous root).</summary>
        Extraneous,

        /// <summary>The root could not be checked numerically; the caller must refuse rather than trust it.</summary>
        Unverifiable,
    }

    /// <summary>
    /// Substitutes a candidate root back into the ORIGINAL equation's difference and checks it vanishes.
    /// This drops radical/rational extraneous roots (sqrt(x)=-2 gives x=4, but sqrt(4)=2≠-2) while
    /// keeping legitimate complex (i, -i) and irrational (√2) roots, whose residual simplifies to an
    /// exact zero. A root that cannot be evaluated numerically is reported <see cref="RootStatus.Unverifiable"/>
    /// so the caller refuses rather than emit it unchecked.
    /// </summary>
    private static RootStatus ValidateRoot(Entity difference, Entity.Variable target, Entity root)
    {
        try
        {
            var residual = difference.Substitute(target, root).Simplify();
            if (!residual.EvaluableNumerical)
            {
                return RootStatus.Unverifiable;
            }

            return residual.EvalNumerical().IsZero ? RootStatus.Valid : RootStatus.Extraneous;
        }
        catch (Exception)
        {
            return RootStatus.Unverifiable;
        }
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
