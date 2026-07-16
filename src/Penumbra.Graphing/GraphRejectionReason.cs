namespace Penumbra.Graphing;

/// <summary>Machine-readable reason a candidate detection did not accept an explicit-function graph.</summary>
public enum GraphRejectionReason
{
    /// <summary>Not rejected (an accepted outcome).</summary>
    None,

    /// <summary>No top-level <c>=</c> relation was found (or a trailing-relation query with no RHS).</summary>
    NotAnEquation,

    /// <summary>The left-hand side is not a single bare variable (e.g. <c>2x=6</c>, <c>x^2+y^2=1</c>).</summary>
    LhsNotBareVariable,

    /// <summary>
    /// The right-hand side has no free variable (e.g. <c>a=2</c>, <c>a=\pi</c>). A definition, not a curve —
    /// see <c>GraphDetector</c>'s remarks for the policy rationale.
    /// </summary>
    ConstantRhs,

    /// <summary>The right-hand side depends on more than one variable (e.g. <c>z=x+y</c>).</summary>
    MultipleFreeVariables,

    /// <summary>
    /// Notation outside the supported grammar: a construct the translator rejects (e.g. <c>\pm</c>), a parse
    /// failure, a chained relation (<c>y=x=2</c>), or a dependent variable that names its own independent
    /// variable (<c>x=x^2</c>).
    /// </summary>
    UnsupportedConstruct,
}
