using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Taffy Seam-1 plumbing: which token runs are grabbable numeric literals (<see cref="LiteralRuns.Find"/>)
/// and how a scrubbed trial value is rebuilt into the line's LaTeX (<see cref="LiteralRuns.Splice"/>).
/// All headless — tokens are built directly, no ink or model involved.
/// </summary>
public sealed class LiteralRunsTests
{
    [Fact]
    public void SingleDigitRunSpansAllTokens()
    {
        RecognizedToken[] tokens = { Tok("1", 0), Tok("2", 20) };

        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        Assert.Equal(0, run.TokenStart);
        Assert.Equal(2, run.TokenCount);
        Assert.Equal("12", run.ValueText);
        Assert.Equal(new InkBounds(0, 0, 30, 10), run.UnionBounds);
        Assert.Equal(tokens.SelectMany(t => t.SourceStrokeIds), run.SourceStrokeIds);
    }

    [Fact]
    public void MultipleRunsSplitByOperatorAndEquals()
    {
        RecognizedToken[] tokens =
        {
            Tok("1", 0), Tok("2", 20), Tok("+", 40), Tok("3", 60), Tok("4", 80), Tok("=", 100),
        };

        IReadOnlyList<LiteralRun> runs = LiteralRuns.Find(tokens);

        Assert.Equal(2, runs.Count);
        Assert.Equal((0, 2, "12"), (runs[0].TokenStart, runs[0].TokenCount, runs[0].ValueText));
        Assert.Equal((3, 2, "34"), (runs[1].TokenStart, runs[1].TokenCount, runs[1].ValueText));
    }

    [Fact]
    public void DecimalRunIncludesTheDot()
    {
        RecognizedToken[] tokens = { Tok("2", 0), Tok(".", 20), Tok("5", 40) };

        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        Assert.Equal("2.5", run.ValueText);
        Assert.Equal(3, run.TokenCount);
    }

    [Fact]
    public void LoneDotIsExcluded()
    {
        RecognizedToken[] tokens = { Tok("+", 0), Tok(".", 20), Tok("=", 40) };

        Assert.Empty(LiteralRuns.Find(tokens));
    }

    [Fact]
    public void RunWithMoreThanOneDotIsExcluded()
    {
        // "1.2.3" is one maximal run of digit/dot tokens but not a mappable literal — dropped whole.
        RecognizedToken[] tokens = { Tok("1", 0), Tok(".", 20), Tok("2", 40), Tok(".", 60), Tok("3", 80) };

        Assert.Empty(LiteralRuns.Find(tokens));
    }

    [Fact]
    public void RunsAreBoundedByControlWords()
    {
        RecognizedToken[] tokens = { Tok("3", 0), Tok(@"\times", 20), Tok("9", 40), Tok("=", 60) };

        IReadOnlyList<LiteralRun> runs = LiteralRuns.Find(tokens);

        Assert.Equal(2, runs.Count);
        Assert.Equal((0, 1, "3"), (runs[0].TokenStart, runs[0].TokenCount, runs[0].ValueText));
        Assert.Equal((2, 1, "9"), (runs[1].TokenStart, runs[1].TokenCount, runs[1].ValueText));
    }

    [Fact]
    public void UnionBoundsCoverAllTokensAndStrokeIdsKeepTokenOrder()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid c = Guid.NewGuid();
        RecognizedToken[] tokens =
        {
            new("4", new[] { a, b }, new InkBounds(0, 5, 10, 20), 1.0),  // two strokes drew the '4'
            new("2", new[] { c }, new InkBounds(12, 0, 8, 10), 1.0),
        };

        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        Assert.Equal(new InkBounds(0, 0, 20, 25), run.UnionBounds);
        Assert.Equal(new[] { a, b, c }, run.SourceStrokeIds);
    }

    [Fact]
    public void SpliceReplacesRunMidExpression()
    {
        RecognizedToken[] tokens = { Tok("x", 0), Tok("=", 20), Tok("5", 40) };
        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        Assert.Equal("x=12", LiteralRuns.Splice(tokens, run, "12"));
    }

    [Fact]
    public void SpliceNegativeParenthesizes()
    {
        // Bare "2+-3" is unproven in the LaTeX translator; the parenthesized group is a known path.
        RecognizedToken[] tokens = { Tok("2", 0), Tok("+", 20), Tok("3", 40), Tok("=", 60) };
        LiteralRun run = LiteralRuns.Find(tokens)[1];
        Assert.Equal("3", run.ValueText);

        Assert.Equal("2+(-3)=", LiteralRuns.Splice(tokens, run, "-3"));
    }

    [Fact]
    public void SplicePreservesTimesNeighbour()
    {
        // The control-word separator must survive the rebuild: "\times" then "12" → "\times 12".
        RecognizedToken[] tokens = { Tok("3", 0), Tok(@"\times", 20), Tok("9", 40), Tok("=", 60) };
        LiteralRun run = LiteralRuns.Find(tokens)[1];
        Assert.Equal("9", run.ValueText);

        Assert.Equal(@"3\times 12=", LiteralRuns.Splice(tokens, run, "12"));
    }

    [Fact]
    public void FindOnEmptyTokenListIsEmpty()
    {
        Assert.Empty(LiteralRuns.Find(Array.Empty<RecognizedToken>()));
    }

    // One token with one fresh source stroke and a 10×10 box at (x, 0).
    private static RecognizedToken Tok(string latex, double x) =>
        new(latex, new[] { Guid.NewGuid() }, new InkBounds(x, 0, 10, 10), 1.0);
}
