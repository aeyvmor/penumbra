using AngouriMath;
using Penumbra.Cas;

namespace Penumbra.Cas.Tests;

/// <summary>
/// Slice 6 practical-algebra correctness and safety sweep. A matrix through translator → analyzer →
/// evaluator covering evaluation, simplification, linear/quadratic/cubic equations, definitions/bindings,
/// polynomial/rational forms, roots/powers, functions, and — the safety half — the explicit solve-target
/// policy, candidate-root substitution validation, denominator/domain guards, parametric refusal, and
/// clean typed refusals in place of raw engine throws.
/// </summary>
/// <remarks>
/// Per the phase contract, evaluator <em>result</em> assertions use mathematical equivalence (both sides
/// parsed with AngouriMath and compared canonically / numerically), not string identity — so a correct
/// answer in any equivalent surface form still passes. Refusals are the existing typed
/// <see cref="EvaluationKind.Error"/> (never a thrown exception, never a silent wrong answer). The
/// fixtures themselves are the executable record of the accepted safety boundary.
/// </remarks>
public sealed class AlgebraSafetySweepTests
{
    private static readonly IReadOnlyDictionary<string, string> NoVars = new Dictionary<string, string>();

    private readonly AngouriMathEvaluator _evaluator = new();

    private EvaluationResult Eval(string latex, IReadOnlyDictionary<string, string>? vars = null) =>
        _evaluator.Evaluate(new EvaluationRequest(latex, vars ?? NoVars));

    // --- equivalence helpers ------------------------------------------------

    /// <summary>True when two AngouriMath-syntax strings denote the same value: their difference
    /// simplifies to an exact zero (handling rational, irrational, and complex forms), or is structurally
    /// zero for the symbolic case.</summary>
    private static bool Equivalent(string amA, string amB)
    {
        var difference = (MathS.FromString(amA) - MathS.FromString(amB)).Simplify();
        if (difference.EvaluableNumerical)
        {
            return difference.EvalNumerical().IsZero;
        }

        return string.Equals(difference.Stringize(), "0", StringComparison.Ordinal);
    }

    /// <summary>The right-hand values of a "x = v1 or x = v2" solution display, as AngouriMath syntax.</summary>
    private static string[] SolutionValues(EvaluationResult result) =>
        result.DisplayText
            .Split(" or ", StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part[(part.IndexOf('=') + 1)..].Trim())
            .ToArray();

    private static void AssertSolutionSet(EvaluationResult result, params string[] expectedAm)
    {
        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Solution, result.Kind);

        var actual = SolutionValues(result);
        Assert.Equal(expectedAm.Length, actual.Length);

        // Order-independent set equality by mathematical equivalence.
        foreach (var expected in expectedAm)
        {
            Assert.Contains(actual, a => Equivalent(a, expected));
        }

        foreach (var a in actual)
        {
            Assert.Contains(expectedAm, expected => Equivalent(a, expected));
        }
    }

    private static void AssertRefused(EvaluationResult result)
    {
        Assert.False(result.IsComputed);
        Assert.Equal(EvaluationKind.Error, result.Kind);
        Assert.NotEmpty(result.DisplayText);
    }

    // === EVALUATION (exact rational / decimal) ==============================

    [Theory]
    [InlineData("2+2", "4")]
    [InlineData(@"\frac{1}{2}+\frac{1}{3}", "5/6")]
    [InlineData(@"\frac{6}{4}", "3/2")]
    [InlineData("2^{10}", "1024")]
    [InlineData(@"\sqrt{16}", "4")]
    [InlineData(@"\sqrt[3]{8}", "2")]
    public void EvaluatesToExactNumber(string latex, string expectedAm)
    {
        var result = Eval(latex);

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.True(Equivalent(result.DisplayText, expectedAm), $"{result.DisplayText} !~ {expectedAm}");
    }

    [Fact]
    public void KeepsRationalExactNotDecimal()
    {
        var result = Eval(@"\frac{1}{3}");

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.True(Equivalent(result.DisplayText, "1/3"));
        Assert.DoesNotContain(".", result.DisplayText); // exact fraction, never a lossy 0.333...
    }

    [Fact]
    public void KeepsIrrationalExact()
    {
        var result = Eval(@"\sqrt{2}");

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.True(Equivalent(result.DisplayText, "sqrt(2)"));
        Assert.DoesNotContain(".", result.DisplayText);
    }

    // === SIMPLIFICATION (no auto-expand / factor) ===========================

    [Fact]
    public void SimplifiesLikeTerms()
    {
        var result = Eval("2x+3x");

        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
        Assert.True(Equivalent(result.DisplayText, "5*x"));
    }

    [Fact]
    public void ProductSimplifiesButIsNotForcedToExpandOrStayFactored()
    {
        // (x+1)(x-1) collapses to x^2-1 (a genuine simplification), and it is mathematically that value.
        var result = Eval("(x+1)(x-1)=");

        Assert.True(Equivalent(result.DisplayText, "x^2-1"));
    }

    [Fact]
    public void SquareIsNotAutoExpanded()
    {
        // Settled decision: Simplify does NOT auto-expand. (x+1)^2 stays a power form, not x^2+2x+1,
        // yet remains mathematically equivalent to the expanded polynomial.
        var result = Eval("(x+1)^2=");

        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
        Assert.True(Equivalent(result.DisplayText, "x^2+2*x+1"));
        Assert.Contains("^", result.DisplayText);              // still a power, i.e. unexpanded
        Assert.DoesNotContain("2 * x", result.DisplayText);    // did not distribute the middle term
    }

    // === LINEAR / QUADRATIC / CUBIC EQUATIONS ===============================

    [Fact]
    public void SolvesLinearEquation()
    {
        EvaluationResult result = Eval("2x+5=13");

        AssertSolutionSet(result, "4");
        Assert.Equal(new SolutionBinding("x", "4"), result.UniqueSolution);
    }

    [Fact]
    public void SolvesQuadraticWithRationalRoots()
    {
        EvaluationResult result = Eval("x^2-5x+6=0");

        AssertSolutionSet(result, "2", "3");
        Assert.Null(result.UniqueSolution);
    }

    [Fact]
    public void SolvesQuadraticWithIrrationalRootsExactly()
    {
        AssertSolutionSet(Eval("x^2=2"), "sqrt(2)", "-sqrt(2)");
    }

    [Fact]
    public void KeepsComplexRootsUnderCurrentComplexDomainPolicy()
    {
        // Settled decision: complex-domain solving is NOT silently switched to real-only.
        AssertSolutionSet(Eval("x^2+1=0"), "i", "-i");
    }

    [Fact]
    public void SolvesCubicWithThreeRoots()
    {
        AssertSolutionSet(Eval("x^3-6x^2+11x-6=0"), "1", "2", "3");
    }

    // === DEFINITIONS / REACTIVE DEPENDENCIES (binding path) =================

    [Fact]
    public void SubstitutesBoundVariableThenEvaluates()
    {
        var result = Eval("2x+3", new Dictionary<string, string> { ["x"] = "5" });

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.True(Equivalent(result.DisplayText, "13"));
    }

    [Fact]
    public void BindingReducingToOneUnknownStillSolves()
    {
        // "x+y=10" is two unknowns, but a supplied y collapses it to a single-unknown solve — the
        // multiple-unknown refusal must NOT fire once a binding has resolved the second unknown.
        var result = Eval("x+y=10", new Dictionary<string, string> { ["y"] = "3" });

        AssertSolutionSet(result, "7");
    }

    // === POLYNOMIAL / RATIONAL EXPRESSIONS ==================================

    [Fact]
    public void RationalExpressionSimplifiesWithDomainGuard()
    {
        // Recorded AngouriMath 1.4.0 behavior: (x^2-1)/(x-1) simplifies to a guarded "x+1 provided
        // not x-1=0" rather than a bare x+1. Pin the guard so a future silent drop is caught.
        var result = Eval("(x^2-1)/(x-1)");

        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
        Assert.Contains("provided", result.DisplayText);
        Assert.Contains("x", result.DisplayText);
    }

    // === ROOTS / POWERS =====================================================

    [Fact]
    public void PowerOfVariableStaysSymbolic()
    {
        var result = Eval("x^2");

        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
        Assert.True(Equivalent(result.DisplayText, "x^2"));
    }

    // === FUNCTIONS ==========================================================

    [Theory]
    [InlineData(@"\sin(0)", "0")]
    [InlineData(@"\cos(\pi)", "-1")]
    [InlineData(@"\ln(e)", "1")]
    [InlineData(@"\log 100", "2")]
    [InlineData("e^0", "1")]
    public void EvaluatesKnownFunctionValues(string latex, string expectedAm)
    {
        var result = Eval(latex);

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.True(Equivalent(result.DisplayText, expectedAm), $"{result.DisplayText} !~ {expectedAm}");
    }

    [Theory]
    [InlineData(@"\sin(x)")]
    [InlineData("e^x")]
    public void KeepsFreeFunctionExpressionsSymbolic(string latex)
    {
        var result = Eval(latex);

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
    }

    // === EXPLICIT ERROR / REFUSAL BEHAVIOR ==================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInputRefuses(string latex)
    {
        AssertRefused(Eval(latex));
    }

    [Fact]
    public void UnsupportedPlusMinusRefusesRatherThanDroppingABranch()
    {
        AssertRefused(Eval(@"2\pm 3="));
    }

    // === SOLVE-TARGET POLICY (multiple unknowns) ============================

    [Fact]
    public void MultipleUnknownEquationRefusesInsteadOfSolvingForTheFirst()
    {
        // BEFORE Slice 6 this silently returned "x = 3 + -3/2 * y" (solved for x alone) — a plausible
        // answer to a question the user did not ask. It must now refuse with a typed outcome.
        var result = Eval("2x+3y=6");

        AssertRefused(result);
        Assert.DoesNotContain("=", result.DisplayText); // not a solution surface
    }

    [Fact]
    public void MultipleUnknownEquationDoesNotSilentlySolveForY()
    {
        var result = Eval("2x+3y=6");

        // Refused, so it cannot be equivalent to either single-variable solve.
        Assert.False(result.IsComputed);
    }

    // === CANDIDATE-SOLUTION VALIDATION ======================================

    [Fact]
    public void RadicalExtraneousRootIsDroppedToNoSolution()
    {
        // sqrt(x)=-2 → AngouriMath offers x=4, but sqrt(4)=+2 ≠ -2. Substituting back drops it; with no
        // surviving candidate the honest answer is "no solution", never the extraneous root.
        var result = Eval(@"\sqrt{x}=-2");

        Assert.Equal(EvaluationKind.Solution, result.Kind);
        Assert.Equal("No solution", result.DisplayText);
    }

    [Fact]
    public void ValidRadicalRootIsKept()
    {
        // The validator must not over-drop: sqrt(x)=2 has the genuine root x=4.
        AssertSolutionSet(Eval(@"\sqrt{x}=2"), "4");
    }

    [Fact]
    public void ValidationKeepsValidRootAndDropsExtraneousInSameEquation()
    {
        // sqrt(x)=x-2 solves to {1, 4}; x=1 fails (sqrt(1)=1 ≠ -1) and is dropped, x=4 holds and stays.
        AssertSolutionSet(Eval(@"\sqrt{x}=x-2"), "4");
    }

    [Fact]
    public void DenominatorZeroingCandidateIsExcluded()
    {
        // x/(x-1)=1/(x-1): the only candidate x=1 zeroes the denominator. Reported as no solution.
        var result = Eval("x/(x-1)=1/(x-1)");

        Assert.Equal(EvaluationKind.Solution, result.Kind);
        Assert.Equal("No solution", result.DisplayText);
    }

    [Fact]
    public void RationalEquationWithNoRootIsNoSolution()
    {
        var result = Eval("1/(x-2)=0");

        Assert.Equal(EvaluationKind.Solution, result.Kind);
        Assert.Equal("No solution", result.DisplayText);
    }

    [Fact]
    public void UncompilableRationalSolveRefusesCleanly()
    {
        // (x^2-x)/(x-1)=0 throws UncompilableNodeException inside AngouriMath's solver. The guard must
        // convert that into a clean typed refusal, never let the raw engine exception escape or leak.
        var result = Eval("(x^2-x)/(x-1)=0");

        AssertRefused(result);
        Assert.DoesNotContain("Providedf", result.DisplayText);
        Assert.DoesNotContain("Exception", result.DisplayText);
    }

    // === PARAMETRIC / INFINITE FAMILIES (absolute value, trig) =============

    [Fact]
    public void AbsoluteValueEquationRefusesInsteadOfEmittingParametricGarbage()
    {
        // Out of scope to solve. AngouriMath returns "3 * e ^ (i * r_1) provided r_1 in RR"; that
        // parametric family must refuse, not surface as a confident answer.
        var result = Eval("|x|=3");

        AssertRefused(result);
        Assert.DoesNotContain("r_1", result.DisplayText);
        Assert.DoesNotContain("e ^", result.DisplayText);
    }

    [Fact]
    public void TrigEquationWithInfiniteFamilyRefuses()
    {
        // sin(x)=0 solves to the infinite family 2*n_1*pi ...; refuse rather than print a parametric root.
        var result = Eval(@"\sin(x)=0");

        AssertRefused(result);
        Assert.DoesNotContain("n_1", result.DisplayText);
    }

    // === MULTIPLE TOP-LEVEL '=' (clean refusal, not an ANTLR dump) ==========

    [Theory]
    [InlineData("x^2-5x+6=0=")]
    [InlineData("2x+3y=6=")]
    public void MultipleEqualsRefusesCleanly(string latex)
    {
        // The trailing extra '=' produced a multi-line ANTLR parser dump in the result text before
        // Slice 6. It must now be a short, clean typed refusal.
        var result = Eval(latex);

        AssertRefused(result);
        Assert.DoesNotContain("mismatched input", result.DisplayText);
        Assert.True(result.DisplayText.Length < 120, "refusal message should be concise, not a parser dump");
    }

    // === CONCRETE RELATIONS still evaluate to a truth value =================

    [Theory]
    [InlineData("2<3", "True")]
    [InlineData("2>3", "False")]
    [InlineData(@"2\lt3", "True")]
    public void ConcreteRelationEvaluatesToBoolean(string latex, string expected)
    {
        var result = Eval(latex);

        Assert.Equal(EvaluationKind.Boolean, result.Kind);
        Assert.Equal(expected, result.DisplayText);
    }
}
