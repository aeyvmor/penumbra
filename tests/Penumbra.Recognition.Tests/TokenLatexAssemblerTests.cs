using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// The 3.9a token-assembly rule, isolated: control words get one separating space, everything else
/// concatenates directly, and a trailing separator is trimmed. The parity tests prove the extraction
/// out of <see cref="ExpressionRecognizer"/> changed nothing — the recognizer's emitted LaTeX is
/// byte-identical to what <see cref="TokenLatexAssembler.Assemble"/> builds from its own tokens.
/// </summary>
public sealed class TokenLatexAssemblerTests
{
    [Theory]
    // Digits concatenate directly — "2""1" must be 21, never a spaced "2 1" (→ 2*1).
    [InlineData(new[] { "2", "1", "+", "7", "=" }, "21+7=")]
    // A control word gets one trailing space so "\pi""x" never fuses into the phantom "\pix".
    [InlineData(new[] { @"\pi", "x" }, @"\pi x")]
    [InlineData(new[] { @"\pi", @"\times", "x", "=" }, @"\pi \times x=")]
    [InlineData(new[] { "3", @"\div", "b", "=" }, @"3\div b=")]
    // A line-final control word's separator is trimmed.
    [InlineData(new[] { @"\pi" }, @"\pi")]
    [InlineData(new string[0], "")]
    public void AssemblesLabelsWithControlWordSeparators(string[] labels, string expected)
    {
        Assert.Equal(expected, TokenLatexAssembler.Assemble(labels));
    }

    [Fact]
    public void NullLabelsThrows()
    {
        Assert.Throws<ArgumentNullException>(() => TokenLatexAssembler.Assemble(null!));
    }

    public static TheoryData<string[]> RecognizerSequences => new()
    {
        new[] { "2", "1", "+", "7", "=" },
        new[] { @"\pi", @"\times", "x", "=" },
        new[] { "3", "x", "7", "=" },      // x→\times rewrite runs before assembly
        new[] { "2", "|", "+", "7", "=" }, // |→1 rewrite runs before assembly
        new[] { @"\sqrt", "x", "=" },
    };

    // Parity property: for real recognizer outputs, re-assembling the Seam-1 token labels must
    // reproduce the emitted LaTeX exactly — otherwise a taffy splice would diverge from the line.
    [Theory]
    [MemberData(nameof(RecognizerSequences))]
    public void AssembleOfResultTokensMatchesResultLatex(string[] labels)
    {
        RecognitionResult result = Recognize(labels);

        Assert.Equal(
            result.Latex,
            TokenLatexAssembler.Assemble(result.Tokens.Select(t => t.Latex).ToList()));
    }

    // Same harness as DigitContextAndAssemblyTests: well-separated strokes, one per scripted label,
    // driven through the REAL recognizer so parity covers segmentation, rewrite, and assembly.
    private static RecognitionResult Recognize(string[] labels)
    {
        var strokes = new List<Stroke>(labels.Length);
        for (int i = 0; i < labels.Length; i++)
        {
            strokes.Add(VLine(i * 100));
        }

        return new ExpressionRecognizer(new OverlapStrokeSegmenter(), new ScriptedClassifier(labels))
            .Recognize(strokes);
    }

    // Returns scripted labels in classification call order = left-to-right groups.
    private sealed class ScriptedClassifier : ISymbolClassifier
    {
        private readonly string[] _labels;
        private int _index;

        public ScriptedClassifier(string[] labels) => _labels = labels;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            new(_labels[_index++], 1.0);
    }

    private static Stroke VLine(double x) =>
        new(Guid.NewGuid(), Enumerable.Range(0, 11)
            .Select(i => new StrokeSample(x, i * 2.0, TimeSpan.Zero, 0.5))
            .ToList());
}
