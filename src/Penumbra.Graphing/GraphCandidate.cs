using AngouriMath;

namespace Penumbra.Graphing;

/// <summary>
/// A detected explicit-function graph candidate <c>dependent = f(independent)</c> (e.g. <c>y = x^2</c>).
/// Produced only by <see cref="IGraphDetector"/> — the constructor is internal so a caller can never assemble
/// a candidate whose <see cref="ExpressionLatex"/> and compiled <see cref="Expression"/> disagree.
/// </summary>
public sealed class GraphCandidate
{
    internal GraphCandidate(
        string dependentVariable, string independentVariable, string expressionLatex, Entity expression)
    {
        DependentVariable = dependentVariable;
        IndependentVariable = independentVariable;
        ExpressionLatex = expressionLatex;
        Expression = expression;
    }

    /// <summary>The left-hand side variable name (e.g. <c>y</c>).</summary>
    public string DependentVariable { get; }

    /// <summary>The single free variable the right-hand side depends on (e.g. <c>x</c>).</summary>
    public string IndependentVariable { get; }

    /// <summary>The right-hand side, in the <c>LatexToAngouriMath</c>-safe LaTeX dialect.</summary>
    public string ExpressionLatex { get; }

    /// <summary>
    /// The parsed right-hand side, ready for <see cref="IDomainSampler"/> to compile into a numeric lambda.
    /// Internal: Graphing is the one engine that touches an AngouriMath <see cref="Entity"/> directly (Sheet
    /// deliberately never does, per ADR-0007) — nothing outside this assembly needs it.
    /// </summary>
    internal Entity Expression { get; }
}
