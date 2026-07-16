using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Recognition;
using Penumbra.Runtime;

namespace Penumbra.Runtime.Tests;

/// <summary>
/// Phase 5.5 slice 4 step 5: <see cref="TaffyLiteralTree"/> locates a <see cref="LiteralRun"/>'s owning node
/// inside an accepted <see cref="LayoutNode"/> tree and rebuilds trial LaTeX by substituting that one node,
/// instead of splicing the flat token list once spatial output has landed. These fixtures run the REAL
/// <see cref="SpatialLayoutParser"/> (no hand-typed tree stand-ins) so the byte-parity fixtures prove the
/// tree path matches <see cref="LiteralRuns.Splice"/> for actual accepted product parses.
/// </summary>
public sealed class TaffyLiteralTreeTests
{
    [Fact]
    public void Discover_WithAcceptedFractionReturnsIndependentNodeScopedLiterals()
    {
        var numerator = new RecognizedToken(
            "1", [Guid.NewGuid()], new InkBounds(0, 0, 10, 10), 1.0);
        var denominator = new RecognizedToken(
            "2", [Guid.NewGuid()], new InkBounds(0, 20, 10, 10), 1.0);
        var bar = new RecognizedToken(
            "-", [Guid.NewGuid()], new InkBounds(0, 14, 10, 1), 1.0);
        RecognizedToken[] tokens = [numerator, denominator, bar];
        var root = new FractionNode(new LeafNode(numerator), new LeafNode(denominator), bar);
        var result = new RecognitionResult(
            @"\frac{1}{2}", tokens, 1.0, 1.0, LayoutParseOutcome.Accepted(root));

        LiteralRun flat = Assert.Single(LiteralRuns.Find(tokens));
        Assert.Equal("12", flat.ValueText);

        IReadOnlyList<TaffyLiteralCandidate> discovered = TaffyLiteralTree.Discover(result);

        Assert.Collection(
            discovered,
            candidate =>
            {
                Assert.Equal("1", candidate.Run.ValueText);
                Assert.Equal(0, candidate.Run.TokenStart);
                Assert.Same(root.Numerator, candidate.Location.Target);
            },
            candidate =>
            {
                Assert.Equal("2", candidate.Run.ValueText);
                Assert.Equal(1, candidate.Run.TokenStart);
                Assert.Same(root.Denominator, candidate.Location.Target);
            });
    }

    [Fact]
    public void Discover_UsesFlatRunsOnlyWhenParseOutcomeIsNull()
    {
        (RecognizedToken[] tokens, _) = new LineBuilder().Add("1").Add("2").Build();
        var legacy = new RecognitionResult("12", tokens, 1.0, 1.0, ParseOutcome: null);
        var refused = new RecognitionResult(
            "12",
            tokens,
            1.0,
            1.0,
            LayoutParseOutcome.Refused(ParseRefusalReason.UnsupportedNotation));

        TaffyLiteralCandidate fallback = Assert.Single(TaffyLiteralTree.Discover(legacy));
        Assert.Equal(TaffyLiteralPath.Flat, fallback.Location.Path);
        Assert.Empty(TaffyLiteralTree.Discover(refused));
    }

    [Fact]
    public void Locate_WithNullParseOutcome_ReturnsFlatFallback()
    {
        (RecognizedToken[] tokens, _) = new LineBuilder().Add("x").Add("=").Add("5").Build();
        var result = new RecognitionResult("x=5", tokens, 0.99, 0.99, ParseOutcome: null);
        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);

        Assert.Equal(TaffyLiteralPath.Flat, location.Path);
        Assert.Null(location.Root);
        Assert.Null(location.Target);
    }

    [Fact]
    public void Locate_WithAcceptedTree_LocatesSingleDigitLeaf()
    {
        RecognitionResult result = Accepted("x=5", new LineBuilder().Add("x").Add("=").Add("5"));
        LiteralRun run = Assert.Single(LiteralRuns.Find(result.Tokens));

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);

        Assert.Equal(TaffyLiteralPath.Tree, location.Path);
        LeafNode leaf = Assert.IsType<LeafNode>(location.Target);
        Assert.Same(result.Tokens[2], leaf.Token);
    }

    [Fact]
    public void Locate_WithAcceptedTree_LocatesMultiDigitSequenceNode()
    {
        RecognitionResult result = Accepted(
            "x=12", new LineBuilder().Add("x").Add("=").Add("1").Add("2"));
        LiteralRun run = Assert.Single(LiteralRuns.Find(result.Tokens));
        Assert.Equal("12", run.ValueText);

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);

        Assert.Equal(TaffyLiteralPath.Tree, location.Path);
        SequenceNode sequence = Assert.IsType<SequenceNode>(location.Target);
        Assert.Equal(2, sequence.Children.Count);
        Assert.Same(result.Tokens[2], Assert.IsType<LeafNode>(sequence.Children[0]).Token);
        Assert.Same(result.Tokens[3], Assert.IsType<LeafNode>(sequence.Children[1]).Token);
    }

    [Fact]
    public void Locate_WithRefusedParseOutcome_RefusesRegardlessOfAnyFlatLiteral()
    {
        // "(x+1" refuses (UnmatchedBracket) but still contains a flat digit run ("1") LiteralRuns.Find would
        // happily identify on tokens alone — the structural verdict must win over that flat read.
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) =
            new LineBuilder().Add("(").Add("x").Add("+").Add("1").Build();
        SpatialParseResult parse = SpatialLayoutParser.Parse(tokens, predictions);
        Assert.Equal(ParseOutcomeKind.Refused, parse.Outcome.Kind);
        var result = new RecognitionResult(
            TokenLatexAssembler.Assemble(parse.Tokens.Select(t => t.Latex).ToList()),
            parse.Tokens,
            0.99,
            0.99,
            parse.Outcome);
        LiteralRun run = Assert.Single(LiteralRuns.Find(result.Tokens));

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);

        Assert.Equal(TaffyLiteralPath.Refused, location.Path);
    }

    [Fact]
    public void Locate_WithAmbiguousParseOutcome_Refuses()
    {
        (RecognizedToken[] tokens, _) = new LineBuilder().Add("x").Add("=").Add("5").Build();
        LayoutParseOutcome ambiguous = LayoutParseOutcome.Ambiguous(ParseRefusalReason.LowMargin, "test tie");
        var result = new RecognitionResult("x=5", tokens, 0.99, 0.99, ambiguous);
        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);

        Assert.Equal(TaffyLiteralPath.Refused, location.Path);
    }

    [Fact]
    public void Locate_WhenAcceptedTreeDoesNotOwnTheRunsExactTokenIdentity_RefusesDefensively()
    {
        // A hand-built tree using a DIFFERENT RecognizedToken object (same Latex, different identity) for
        // the digit than the one the region's own Tokens list carries — TaffyLiteralTree must never treat
        // value-equal-but-distinct tokens as a match; it refuses instead of silently grabbing the wrong node.
        (RecognizedToken[] tokens, _) = new LineBuilder().Add("x").Add("=").Add("5").Build();
        var impostor = new RecognizedToken("5", tokens[2].SourceStrokeIds, tokens[2].Bounds, tokens[2].Confidence);
        LayoutNode impostorRoot = new RelationNode(new LeafNode(tokens[0]), tokens[1], new LeafNode(impostor));
        var result = new RecognitionResult("x=5", tokens, 0.99, 0.99, LayoutParseOutcome.Accepted(impostorRoot));
        LiteralRun run = Assert.Single(LiteralRuns.Find(tokens));

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);

        Assert.Equal(TaffyLiteralPath.Refused, location.Path);
    }

    [Fact]
    public void BuildTrialLatex_WhenLocationIsNotTreePath_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => TaffyLiteralTree.BuildTrialLatex(TaffyLiteralLocation.FlatFallback, "6"));
        Assert.Throws<InvalidOperationException>(
            () => TaffyLiteralTree.BuildTrialLatex(TaffyLiteralLocation.StructuralRefusal, "6"));
    }

    [Theory]
    [InlineData("x=5", new[] { "x", "=", "5" }, 0, "7")]
    [InlineData("x=5", new[] { "x", "=", "5" }, 0, "-3")]
    [InlineData("x=12", new[] { "x", "=", "1", "2" }, 0, "7")]
    [InlineData("x=12", new[] { "x", "=", "1", "2" }, 0, "123")]
    [InlineData("2x+5=13", new[] { "2", "x", "+", "5", "=", "1", "3" }, 0, "9")]
    [InlineData("2x+5=13", new[] { "2", "x", "+", "5", "=", "1", "3" }, 2, "-8")]
    public void BuildTrialLatex_IsByteIdenticalToFlatSplice(
        string expectedLatex, string[] labels, int literalRunIndex, string replacement)
    {
        RecognitionResult result = Accepted(expectedLatex, LabelsToBuilder(labels));
        IReadOnlyList<LiteralRun> runs = LiteralRuns.Find(result.Tokens);
        LiteralRun run = runs[literalRunIndex];

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);
        Assert.Equal(TaffyLiteralPath.Tree, location.Path);
        string treeLatex = TaffyLiteralTree.BuildTrialLatex(location, replacement);
        string flatLatex = LiteralRuns.Splice(result.Tokens, run, replacement);

        Assert.Equal(flatLatex, treeLatex);
    }

    [Fact]
    public void BuildTrialLatex_SubstitutesOnlyTheTargetLiteralInsideAnImplicitProduct()
    {
        // "12(3)" — implicit product of a two-digit number and a delimited group. Scrubbing the "12" must
        // leave the group's "3" (and its brackets) completely untouched.
        RecognitionResult result = Accepted(
            @"12\left(3\right)", new LineBuilder().Add("1").Add("2").Add("(").Add("3").Add(")"));
        LiteralRun run = LiteralRuns.Find(result.Tokens).Single(candidate => candidate.ValueText == "12");

        TaffyLiteralLocation location = TaffyLiteralTree.Locate(result, run);
        string treeLatex = TaffyLiteralTree.BuildTrialLatex(location, "9");

        Assert.Equal(@"9\left(3\right)", treeLatex);
    }

    private static RecognitionResult Accepted(string expectedLatex, LineBuilder builder)
    {
        (RecognizedToken[] tokens, SymbolPrediction[] predictions) = builder.Build();
        SpatialParseResult parse = SpatialLayoutParser.Parse(tokens, predictions);
        Assert.True(parse.Outcome.IsAccepted, $"expected an accepted parse: {parse.Outcome.Reason}");
        string latex = LayoutLatexSerializer.Serialize(parse.Outcome.Root!);
        Assert.Equal(expectedLatex, latex);
        return new RecognitionResult(latex, parse.Tokens, 0.99, 0.99, parse.Outcome);
    }

    private static LineBuilder LabelsToBuilder(IReadOnlyList<string> labels)
    {
        var builder = new LineBuilder();
        foreach (string label in labels)
        {
            builder.Add(label);
        }
        return builder;
    }

    /// <summary>Auto-positions tokens left-to-right on a shared baseline with a non-tight default gap.</summary>
    private sealed class LineBuilder
    {
        private readonly List<RecognizedToken> _tokens = new();
        private readonly List<SymbolPrediction> _predictions = new();
        private double _cursor;

        public LineBuilder Add(string latex, double gap = 16, double width = 12, double height = 20)
        {
            double x = _tokens.Count == 0 ? 0 : _cursor + gap;
            _tokens.Add(new RecognizedToken(latex, new[] { Guid.NewGuid() }, new InkBounds(x, 0, width, height), 1.0));
            _predictions.Add(new SymbolPrediction(latex, 1.0));
            _cursor = x + width;
            return this;
        }

        public (RecognizedToken[] Tokens, SymbolPrediction[] Predictions) Build() =>
            (_tokens.ToArray(), _predictions.ToArray());
    }
}
