using Penumbra.Cas;

namespace Penumbra.Cas.Tests;

/// <summary>
/// Golden fixture table for the expression analyzer (Seam 2's first CAS question). Each row pins a
/// LaTeX expression to the structural facts the sheet graph binds on: the symbol it defines, the
/// variables it depends on, and whether it is a trailing-<c>=</c> query. Add a row before fixing any
/// classification bug — this is the de-risking net that keeps the graph CAS-agnostic.
/// </summary>
public sealed class AngouriMathExpressionAnalyzerTests
{
    private readonly AngouriMathExpressionAnalyzer _analyzer = new();

    [Theory]
    // plain definitions: single symbol = value
    [InlineData("x=5", "x", "", false)]
    [InlineData("a=-3", "a", "", false)]
    [InlineData(@"c=\frac{1}{2}", "c", "", false)]
    // definitions with an expression on the RHS: depend on the RHS variables
    [InlineData("y=x+2", "y", "x", false)]
    [InlineData("z=x+y", "z", "x,y", false)]
    [InlineData("w=2a+3b", "w", "a,b", false)]
    // a self-referencing definition keeps its own symbol as a dependency — that's how cycles surface
    [InlineData("a=a+1", "a", "a", false)]
    // subscripts fold to names AngouriMath reads as powers ("x1" parses as x^1), so a subscripted LHS
    // is NOT a definition — variables are single letters today (3.9 token-assembly; kickoff traps).
    // The evaluator agrees: a binding named "x1" could never substitute into the parsed x^1.
    [InlineData("x_1=5", null, "x", false)]
    [InlineData("v_0=v_1+1", null, "v", false)]
    // multi-letter greek names are real AngouriMath variables, so they define normally
    [InlineData(@"\theta=1", "theta", "", false)]
    // trailing-"=" queries: no defined symbol, depend on what they compute over
    [InlineData("2+3=", null, "", true)]
    [InlineData("2+x=", null, "x", true)]
    [InlineData("y+1=", null, "y", true)]
    // full equations / statements: not definitions, not queries; every variable is a dependency
    [InlineData("2x+3=7", null, "x", false)]
    [InlineData("x+y=10", null, "x,y", false)]
    [InlineData("2+2=4", null, "", false)]
    // bare expressions without "=": statements over their variables
    [InlineData("x+1", null, "x", false)]
    [InlineData("2+2", null, "", false)]
    [InlineData("ab", null, "a,b", false)]
    // constants (pi, e) are resolved by the CAS, never surfaced as free variables
    [InlineData(@"\pi+x=", null, "x", true)]
    [InlineData(@"r=2\pi", "r", "", false)]
    public void ClassifiesExpression(string latex, string? expectedSymbol, string expectedVars, bool expectedQuery)
    {
        var analysis = _analyzer.Analyze(latex);

        Assert.Equal(expectedSymbol, analysis.DefinedSymbol);
        Assert.Equal(ParseVars(expectedVars), analysis.FreeVariables.OrderBy(v => v));
        Assert.Equal(expectedQuery, analysis.IsQuery);
    }

    [Theory]
    // blank / whitespace / null
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    // rejected construct (\pm throws in the translator) and outright garbage must analyse, not crash
    [InlineData(@"2\pm 3=")]
    [InlineData("garbage^^^")]
    [InlineData(")(")]
    [InlineData("==")]
    [InlineData(@"\frac{}{}")]
    public void MalformedInputYieldsSafeEmptyAnalysisWithoutThrowing(string? latex)
    {
        var analysis = _analyzer.Analyze(latex!);

        Assert.Null(analysis.DefinedSymbol);
        Assert.Empty(analysis.FreeVariables);
        Assert.False(analysis.IsQuery);
    }

    private static IEnumerable<string> ParseVars(string csv) =>
        string.IsNullOrEmpty(csv)
            ? Enumerable.Empty<string>()
            : csv.Split(',').OrderBy(v => v);
}
