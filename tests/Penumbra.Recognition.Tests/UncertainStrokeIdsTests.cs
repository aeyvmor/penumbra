using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 4.5c: the glitch-ink set — exactly the strokes of below-threshold tokens, nobody else's.
/// This is the headless half of "the reject threshold gets a visible body".
/// </summary>
public sealed class UncertainStrokeIdsTests
{
    [Fact]
    public void OnlyBelowThresholdTokens_ContributeTheirStrokes()
    {
        var confident = Guid.NewGuid();
        var shakyA = Guid.NewGuid();
        var shakyB = Guid.NewGuid();

        var result = new RecognitionResult("2+", new[]
        {
            Token("2", 0.9, confident),
            Token("+", 0.4, shakyA, shakyB),   // a two-stroke '+' below the bar
        }, 0.65, 0.4);

        IReadOnlySet<Guid> ids = RecognitionGate.UncertainStrokeIds(result, threshold: 0.55);

        Assert.Equal(2, ids.Count);
        Assert.Contains(shakyA, ids);
        Assert.Contains(shakyB, ids);
        Assert.DoesNotContain(confident, ids);
    }

    [Fact]
    public void ConfidentRead_YieldsEmptySet()
    {
        var result = new RecognitionResult("2", new[] { Token("2", 0.9, Guid.NewGuid()) }, 0.9, 0.9);

        Assert.Empty(RecognitionGate.UncertainStrokeIds(result, threshold: 0.55));
    }

    [Fact]
    public void ExactlyAtThreshold_IsNotUncertain()
    {
        // Mirrors the gate: MinConfidence >= threshold accepts, so < is the uncertainty test.
        var result = new RecognitionResult("2", new[] { Token("2", 0.55, Guid.NewGuid()) }, 0.55, 0.55);

        Assert.Empty(RecognitionGate.UncertainStrokeIds(result, threshold: 0.55));
    }

    private static RecognizedToken Token(string latex, double confidence, params Guid[] strokeIds) =>
        new(latex, strokeIds, new InkBounds(0, 0, 10, 10), confidence);
}
