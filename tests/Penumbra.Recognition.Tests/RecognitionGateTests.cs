using Penumbra.Core;
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
