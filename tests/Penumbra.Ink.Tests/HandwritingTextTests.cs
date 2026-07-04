using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>
/// The CAS DisplayText → handwriting-text translation: '*' becomes juxtaposition next to a variable and a
/// '×' between digits, while every other whitespace is preserved so multi-term answers keep their word gaps.
/// </summary>
public sealed class HandwritingTextTests
{
    [Theory]
    // Juxtaposition: a letter on either side of '*' drops the operator and its padding.
    [InlineData("4 * y", "4y")]
    [InlineData("2 * x * y", "2xy")]
    [InlineData("4*y", "4y")]           // no spaces, still juxtaposed
    [InlineData("y * 4", "y4")]         // letter on the left
    // Between digits '*' becomes an explicit times sign so "3*4" never merges into "34".
    [InlineData("3 * 4", "3×4")]
    [InlineData("3*4", "3×4")]
    // No multiplication: preserved verbatim, word gaps and all.
    [InlineData("x = 2 or x = -2", "x = 2 or x = -2")]
    [InlineData("1/3", "1/3")]
    [InlineData("-4", "-4")]
    [InlineData("sqrt(2)", "sqrt(2)")]
    [InlineData("No solution", "No solution")]
    public void FromDisplayText_RewritesMultiplicationOnly(string input, string expected)
    {
        Assert.Equal(expected, HandwritingText.FromDisplayText(input));
    }

    [Fact]
    public void FromDisplayText_NullThrows()
    {
        Assert.Throws<ArgumentNullException>(() => HandwritingText.FromDisplayText(null!));
    }
}
