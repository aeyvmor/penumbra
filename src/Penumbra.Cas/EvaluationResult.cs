namespace Penumbra.Cas;

/// <summary>What kind of answer an <see cref="IEvaluator"/> produced.</summary>
public enum EvaluationKind
{
    /// <summary>No real computation happened (placeholder / not yet wired).</summary>
    Pending,

    /// <summary>A concrete number (or exact value such as <c>1/3</c>, <c>sqrt(2)</c>).</summary>
    Number,

    /// <summary>A simplified expression that still contains free variables.</summary>
    Symbolic,

    /// <summary>One or more roots of an equation solved for an unknown.</summary>
    Solution,

    /// <summary>A truth value (e.g. evaluating <c>2 + 2 = 4</c>).</summary>
    Boolean,

    /// <summary>The input could not be parsed or evaluated; see <see cref="EvaluationResult.DisplayText"/>.</summary>
    Error,
}

/// <summary>One verified equation solution that can safely act as a reactive-sheet value.</summary>
/// <param name="Symbol">The equation's solved variable.</param>
/// <param name="Latex">The exact solved value only, without the variable or equals sign.</param>
public sealed record SolutionBinding(string Symbol, string Latex);

/// <summary>The result of evaluating a mathematical expression.</summary>
/// <param name="Latex">LaTeX of the result, ready to render back as ink.</param>
/// <param name="DisplayText">A plain-text form of the result (or an error message).</param>
/// <param name="IsComputed">True when evaluation succeeded.</param>
/// <param name="Kind">The category of result, for rendering decisions.</param>
/// <param name="UniqueSolution">The sole verified binding when an equation has exactly one solution;
/// null for ordinary values, no solution, multiple roots, or unverifiable/parametric results.</param>
public sealed record EvaluationResult(
    string Latex,
    string DisplayText,
    bool IsComputed,
    EvaluationKind Kind = EvaluationKind.Pending,
    SolutionBinding? UniqueSolution = null);
