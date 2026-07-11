using Penumbra.Cas.Latex;

namespace Penumbra.Cas.Tests;

/// <summary>
/// Golden fixture table for the LaTeX → AngouriMath translator. Each row pins a LaTeX construct
/// to the exact AngouriMath syntax we expect to feed the parser. This is the de-risking net for
/// the finicky translation seam — add a row before fixing any translation bug.
/// </summary>
public sealed class LatexToAngouriMathTests
{
    [Theory]
    // arithmetic & implicit multiplication
    [InlineData("2+2", "2+2")]
    [InlineData("2x+3", "2*x+3")]
    [InlineData("xy", "x*y")]
    [InlineData("3xy", "3*x*y")]
    [InlineData("2(x+1)", "2*(x+1)")]
    [InlineData("(x+1)(x-1)", "(x+1)*(x-1)")]
    [InlineData("-5", "-5")]
    [InlineData("3.14", "3.14")]
    // Phase 5: a taffy splice emits negative trial values parenthesized ("2+(-3)"), never bare "+-".
    [InlineData("2+(-3)", "2+(-3)")]
    // fractions
    [InlineData(@"\frac{1}{2}", "((1)/(2))")]
    [InlineData(@"\frac{x+1}{2}", "((x+1)/(2))")]
    [InlineData(@"\frac{1}{2}+\frac{1}{3}", "((1)/(2))+((1)/(3))")]
    [InlineData(@"\dfrac{a}{b}", "((a)/(b))")]
    // powers & roots
    [InlineData("x^2", "x^(2)")]
    [InlineData("x^{n+1}", "x^(n+1)")]
    [InlineData("a^2+b^2", "a^(2)+b^(2)")]
    [InlineData(@"\sqrt{2}", "sqrt(2)")]
    [InlineData(@"\sqrt{x+1}", "sqrt(x+1)")]
    [InlineData(@"\sqrt[3]{8}", "((8)^(1/(3)))")]
    // functions
    [InlineData(@"\sin x", "sin(x)")]
    [InlineData(@"\sin(2x)", "sin(2*x)")]
    [InlineData(@"\cos(\pi)", "cos(pi)")]
    [InlineData(@"\ln(x)", "ln(x)")]
    [InlineData(@"\log 100", "log(10,100)")]
    [InlineData(@"\log_2 8", "log(2,8)")]
    // operators & symbols
    [InlineData(@"3\cdot4", "3*4")]
    [InlineData(@"2 \times 3", "2*3")]
    [InlineData(@"6 \div 2", "6/2")]
    [InlineData(@"2\pi", "2*pi")]
    [InlineData(@"\frac{\pi}{2}", "((pi)/(2))")]
    [InlineData(@"\tau", "(2*pi)")]
    [InlineData(@"\theta", "theta")]
    [InlineData(@"\alpha+\beta", "alpha+beta")]
    [InlineData("|x|", "abs(x)")]
    [InlineData(@"\left(x+1\right)", "(x+1)")]
    // 3.9f: \left / \right decorate the delimiter; a function must read past them to its argument.
    [InlineData(@"\sin\left(x\right)", "sin(x)")]
    [InlineData(@"\cos\left(2x\right)", "cos(2*x)")]
    [InlineData(@"\left(\left(x\right)\right)", "((x))")]
    // subscripts fold into the variable name
    [InlineData("x_1", "x1")]
    [InlineData("v_{0}", "v0")]
    // equations keep the relational operator
    [InlineData("2x+3=7", "2*x+3=7")]
    [InlineData("2+3=", "2+3=")]
    // Phase 5: a parenthesized negative on the right-hand side (taffy splice of "x=5" scrubbed below 0).
    [InlineData("x=(-5)", "x=(-5)")]
    public void TranslatesToAngouriMathSyntax(string latex, string expected)
    {
        Assert.Equal(expected, LatexToAngouriMath.Translate(latex));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankInputTranslatesToEmpty(string? latex)
    {
        Assert.Equal(string.Empty, LatexToAngouriMath.Translate(latex!));
    }

    [Fact]
    public void NestedFractionTranslatesWithBalancedParentheses()
    {
        // \frac{\frac{1}{2}}{3} → (( ((1)/(2)) )/(3))
        Assert.Equal("((((1)/(2)))/(3))", LatexToAngouriMath.Translate(@"\frac{\frac{1}{2}}{3}"));
    }

    [Fact]
    public void PlusMinusIsRejectedRatherThanSilentlyDroppingABranch()
    {
        // 3.9f: "a \pm b" is two answers; emitting only "+" would be a silent wrong single answer.
        // Reject loudly instead — \pm is a recognizer class, so it can arrive from real ink.
        var ex = Assert.Throws<NotSupportedException>(() => LatexToAngouriMath.Translate(@"2\pm 3="));

        Assert.Contains("pm", ex.Message);
    }
}
