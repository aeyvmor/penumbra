using Penumbra.Cas;

namespace Penumbra.Cas.Tests;

/// <summary>
/// End-to-end CAS behaviour: LaTeX in → correct answer out. This is the Phase 2 "Done when"
/// proof — evaluate, simplify, solve, and flat variable binding, all headless.
/// </summary>
public sealed class AngouriMathEvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, string> NoVars = new Dictionary<string, string>();

    private readonly AngouriMathEvaluator _evaluator = new();

    private EvaluationResult Eval(string latex, IReadOnlyDictionary<string, string>? vars = null) =>
        _evaluator.Evaluate(new EvaluationRequest(latex, vars ?? NoVars));

    [Theory]
    [InlineData("2+2", "4")]
    [InlineData(@"\frac{1}{2}+\frac{1}{3}", "5/6")]
    [InlineData(@"\frac{6}{4}", "3/2")]
    [InlineData(@"\sqrt{16}", "4")]
    [InlineData("2^{10}", "1024")]
    [InlineData(@"\frac{1}{3}", "1/3")]
    [InlineData("x-x", "0")]
    public void EvaluatesNumericExpressions(string latex, string expected)
    {
        var result = Eval(latex);

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.Equal(expected, result.DisplayText);
    }

    [Fact]
    public void KeepsIrrationalResultsExact()
    {
        var result = Eval(@"\sqrt{2}");

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.Equal("sqrt(2)", result.DisplayText);
        Assert.Equal(@"\sqrt{2}", result.Latex);
    }

    [Fact]
    public void ResultCarriesLatexOfTheAnswer()
    {
        // \frac{1}{2}+\frac{1}{3} = 5/6, and the result LaTeX renders that fraction.
        var result = Eval(@"\frac{1}{2}+\frac{1}{3}");

        Assert.Equal(@"\frac{5}{6}", result.Latex);
    }

    [Fact]
    public void SimplifiesSymbolicExpression()
    {
        var result = Eval("2x+3x");

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
        Assert.Equal("5 * x", result.DisplayText);
    }

    [Fact]
    public void SolvesLinearEquation()
    {
        var result = Eval("2x+3=7");

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Solution, result.Kind);
        Assert.Equal("x = 2", result.DisplayText);
    }

    [Fact]
    public void SolvesQuadraticWithBothRoots()
    {
        var result = Eval("x^2=4");

        Assert.Equal(EvaluationKind.Solution, result.Kind);
        Assert.Contains("x = 2", result.DisplayText);
        Assert.Contains("x = -2", result.DisplayText);
    }

    [Fact]
    public void EvaluatesTrueEqualityAssertion()
    {
        var result = Eval("2+2=4");

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Boolean, result.Kind);
        Assert.Equal("True", result.DisplayText);
    }

    [Fact]
    public void EvaluatesFalseEqualityAssertion()
    {
        var result = Eval("2+2=5");

        Assert.Equal(EvaluationKind.Boolean, result.Kind);
        Assert.Equal("False", result.DisplayText);
    }

    [Fact]
    public void SubstitutesBoundVariableThenEvaluates()
    {
        var result = Eval("2x+3", new Dictionary<string, string> { ["x"] = "5" });

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.Equal("13", result.DisplayText);
    }

    [Fact]
    public void TrailingEqualsWithBindingComputesLeftHandSide()
    {
        // "2x+3=" with x bound is a "compute this", not an equation to solve.
        var result = Eval("2x+3=", new Dictionary<string, string> { ["x"] = "5" });

        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.Equal("13", result.DisplayText);
    }

    [Fact]
    public void BindingFeedsEquationSolve()
    {
        var result = Eval("x+y=10", new Dictionary<string, string> { ["y"] = "3" });

        Assert.Equal(EvaluationKind.Solution, result.Kind);
        Assert.Equal("x = 7", result.DisplayText);
    }

    [Fact]
    public void MultipleBindingsResolveToNumber()
    {
        var result = Eval("a+b", new Dictionary<string, string> { ["a"] = "3", ["b"] = "4" });

        Assert.Equal("7", result.DisplayText);
    }

    [Fact]
    public void UnboundExpressionStaysSymbolic()
    {
        var result = Eval("x+1");

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, result.Kind);
        Assert.Contains("x", result.DisplayText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInputIsNotComputed(string latex)
    {
        var result = Eval(latex);

        Assert.False(result.IsComputed);
        Assert.Equal(EvaluationKind.Error, result.Kind);
    }

    [Fact]
    public void EvaluatesInsideSizedDelimiters()
    {
        // 3.9f: \left( ... \right) must evaluate exactly like plain parens — here (2+3)= is 5.
        var result = Eval(@"\left(2+3\right)=");

        Assert.True(result.IsComputed);
        Assert.Equal(EvaluationKind.Number, result.Kind);
        Assert.Equal("5", result.DisplayText);
    }

    [Fact]
    public void PlusMinusIsRejectedNotSilentlyAnswered()
    {
        // 3.9f: "2 \pm 3" has two answers (5 and -1); the evaluator must surface a graceful Error
        // rather than the silent single-branch "5" that dropping the minus branch would give.
        var result = Eval(@"2\pm 3=");

        Assert.False(result.IsComputed);
        Assert.Equal(EvaluationKind.Error, result.Kind);
        Assert.NotEqual("5", result.DisplayText);
        Assert.Contains("pm", result.DisplayText);
    }

    [Fact]
    public void MalformedInputFailsGracefully()
    {
        var result = Eval("garbage^^^");

        Assert.False(result.IsComputed);
        Assert.Equal(EvaluationKind.Error, result.Kind);
        Assert.NotEmpty(result.DisplayText);
    }
}
