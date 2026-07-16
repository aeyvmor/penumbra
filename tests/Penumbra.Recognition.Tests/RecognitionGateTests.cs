using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// 3.9c reject gate: a read is acted on only when every symbol clears the threshold; otherwise it is
/// refused with the 1-based position of the weakest symbol.
/// </summary>
public sealed class RecognitionGateTests
{
    private const double Threshold = 0.55;

    [Fact]
    public void AllAboveThreshold_Accepts()
    {
        RecognitionResult result = Result((0.9, "2"), (0.8, "+"), (0.7, "2"), (0.95, "="));

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.True(gate.Accepted);
        Assert.Null(gate.Refusal);
        Assert.Equal(0, gate.SymbolPosition);
    }

    [Fact]
    public void BelowThreshold_RejectsAndNamesWeakestSymbolByOneBasedPosition()
    {
        // The third symbol (0.30) is the weakest and below the bar.
        RecognitionResult result = Result((0.9, "2"), (0.8, "+"), (0.30, "2"), (0.95, "="));

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.False(gate.Accepted);
        Assert.Equal(3, gate.SymbolPosition);
        Assert.Equal("couldn't read that (symbol 3 looks ambiguous)", gate.Refusal);
    }

    [Fact]
    public void EmptyResult_Accepts()
    {
        var result = new RecognitionResult(string.Empty, Array.Empty<RecognizedToken>(), 0, 0);

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.True(gate.Accepted);
        Assert.Null(gate.Refusal);
    }

    [Fact]
    public void ExactlyAtThreshold_Accepts()
    {
        RecognitionResult result = Result((Threshold, "x"));

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.True(gate.Accepted);
    }

    // ---- Phase 5.5 slice 4: structural refusal ------------------------------------------------------------

    [Fact]
    public void NullParseOutcome_CarriesNoStructuralOpinion_ConfidentResultAccepts()
    {
        // Every pre-existing construction site (test fakes, NoOpRecognizer) never sets ParseOutcome —
        // confirms behaviour for those callers is unchanged byte-for-byte by this slice.
        RecognitionResult result = Result((0.9, "2"), (0.8, "+"), (0.7, "2"), (0.95, "="));
        Assert.Null(result.ParseOutcome);

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.True(gate.Accepted);
    }

    [Fact]
    public void RefusedParseOutcome_WithConfidentTokens_RefusesStructurally()
    {
        RecognitionResult result = Result((0.9, "("), (0.9, "x"), (0.9, "+"), (0.9, "1")) with
        {
            ParseOutcome = LayoutParseOutcome.Refused(ParseRefusalReason.UnmatchedBracket),
        };

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.False(gate.Accepted);
        Assert.Equal(0, gate.SymbolPosition);
        Assert.Equal("couldn't read that (a bracket doesn't have a match)", gate.Refusal);
    }

    [Fact]
    public void AmbiguousParseOutcome_WithConfidentTokens_RefusesStructurally()
    {
        RecognitionResult result = Result((0.9, "o")) with
        {
            ParseOutcome = LayoutParseOutcome.Ambiguous(ParseRefusalReason.LowMargin),
        };

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.False(gate.Accepted);
        Assert.Equal("couldn't read that (two readings looked equally likely)", gate.Refusal);
    }

    [Fact]
    public void AcceptedParseOutcome_WithConfidentTokens_Accepts()
    {
        RecognitionResult result = Result((0.9, "2"), (0.9, "+"), (0.9, "2")) with
        {
            ParseOutcome = LayoutParseOutcome.Accepted(new LeafNode(
                new RecognizedToken("2", Array.Empty<Guid>(), default, 0.9))),
        };

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.True(gate.Accepted);
        Assert.Null(gate.Refusal);
    }

    [Fact]
    public void LowConfidenceToken_TakesPrecedenceOverStructuralRefusal()
    {
        // Both gates would refuse this line — the per-symbol confidence gate must win (a shaky glyph is
        // more actionable to name than a shape-level message), per the documented precedence.
        RecognitionResult result = Result((0.9, "("), (0.30, "x"), (0.9, "+"), (0.9, "1")) with
        {
            ParseOutcome = LayoutParseOutcome.Refused(ParseRefusalReason.UnmatchedBracket),
        };

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.False(gate.Accepted);
        Assert.Equal(2, gate.SymbolPosition);
        Assert.Equal("couldn't read that (symbol 2 looks ambiguous)", gate.Refusal);
    }

    [Theory]
    [InlineData(ParseRefusalReason.UncertainScript, "couldn't read that (a symbol's position looks like a script, not plain text)")]
    [InlineData(ParseRefusalReason.EmptyRadicalOwnership, "couldn't read that (that radical isn't supported yet)")]
    [InlineData(ParseRefusalReason.AmbiguousFunctionWord, "couldn't read that (a function name has no argument)")]
    [InlineData(ParseRefusalReason.UnsupportedRelation, "couldn't read that (that relation isn't supported yet)")]
    [InlineData(ParseRefusalReason.UnsupportedNotation, "couldn't read that (that notation isn't supported yet)")]
    [InlineData(ParseRefusalReason.LostStroke, "couldn't read that (a stroke got lost while reading)")]
    [InlineData(ParseRefusalReason.DoubleOwnership, "couldn't read that (a stroke got read twice)")]
    public void StructuralRefusal_UsesAReasonSpecificMessage(ParseRefusalReason reason, string expected)
    {
        RecognitionResult result = Result((0.9, "x")) with
        {
            ParseOutcome = LayoutParseOutcome.Refused(reason),
        };

        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, Threshold);

        Assert.Equal(expected, gate.Refusal);
    }

    private static RecognitionResult Result(params (double Confidence, string Label)[] symbols)
    {
        var tokens = symbols
            .Select(s => new RecognizedToken(s.Label, Array.Empty<Guid>(), default, s.Confidence))
            .ToList();
        double mean = symbols.Average(s => s.Confidence);
        double min = symbols.Min(s => s.Confidence);
        return new RecognitionResult(string.Concat(symbols.Select(s => s.Label)), tokens, mean, min);
    }
}
