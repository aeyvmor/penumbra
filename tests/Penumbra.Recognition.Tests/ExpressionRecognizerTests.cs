using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// 5c/5d de-risking: segment → classify-each → assemble. A fake classifier isolates the
/// assembly/grammar/Seam-1 logic deterministically; the real ONNX model proves the wiring
/// end-to-end on one symbol.
/// </summary>
public sealed class ExpressionRecognizerTests
{
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "Models");

    [Fact]
    public void Assembles_Labels_LeftToRight_AndRealizesSeam1()
    {
        // Four well-separated symbols; the fake labels each by its x position.
        Stroke a = VLine(0);
        Stroke bH = HLine(38, 10);
        Stroke bV = VLine(48);   // a + b form a 2-stroke '+' in the middle
        Stroke c = VLine(90);
        Stroke d = VLine(130);

        var classifier = new FakeClassifier(box =>
            box.X < 20 ? "2" : box.X < 70 ? "+" : box.X < 110 ? "2" : "=");

        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);
        RecognitionResult result = recognizer.Recognize(new[] { a, bH, bV, c, d });

        Assert.Equal("2+2=", result.Latex);
        Assert.Equal(4, result.Tokens.Count);

        // Seam 1: the '+' token (second, left-to-right) maps back to both of its strokes.
        RecognizedToken plus = result.Tokens[1];
        Assert.Equal("+", plus.Latex);
        Assert.Equal(2, plus.SourceStrokeIds.Count);
        Assert.Contains(bH.Id, plus.SourceStrokeIds);
        Assert.Contains(bV.Id, plus.SourceStrokeIds);

        for (int i = 1; i < result.Tokens.Count; i++)
        {
            Assert.True(result.Tokens[i - 1].Bounds.X <= result.Tokens[i].Bounds.X);
        }
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), new FakeClassifier(_ => "x"));

        RecognitionResult result = recognizer.Recognize(Array.Empty<Stroke>());

        Assert.Equal(string.Empty, result.Latex);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void PassesLineContext_NotSelfContext_AndAveragesConfidence()
    {
        // Four separated symbols with DISTINCT heights (10, 20, 30, 40), left-to-right.
        Stroke a = VBar(0, 10);
        Stroke b = VBar(40, 20);
        Stroke c = VBar(80, 30);
        Stroke d = VBar(120, 40);

        var spy = new SpyClassifier(("a", 0.8), ("b", 0.9), ("c", 0.7), ("d", 0.6));
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), spy);
        RecognitionResult result = recognizer.Recognize(new[] { a, b, c, d });

        Assert.Equal("abcd", result.Latex);
        Assert.Equal(4, spy.Contexts.Count);

        // Every symbol is judged against the SAME line context — not its own self-context.
        SymbolContext context = spy.Contexts[0];
        Assert.All(spy.Contexts, seen => Assert.Equal(context, seen));
        Assert.Equal(25.0, context.RefHeight, 3);   // median of {10,20,30,40} (even-count path)
        Assert.Equal(0.0, context.ExprYMin, 3);
        Assert.Equal(40.0, context.ExprHeight, 3);  // the tallest spans 0..40

        // Result confidence is the mean of the per-symbol confidences.
        Assert.Equal((0.8 + 0.9 + 0.7 + 0.6) / 4, result.Confidence, 3);

        // 3.9c: MinConfidence is the weakest symbol — here the last one (0.6).
        Assert.Equal(0.6, result.MinConfidence, 3);
    }

    [Fact]
    public void EmptyInput_MinConfidenceIsZero()
    {
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), new FakeClassifier(_ => "x"));

        RecognitionResult result = recognizer.Recognize(Array.Empty<Stroke>());

        Assert.Equal(0, result.MinConfidence);
    }

    [Fact]
    public void RealModel_ReadsASyntheticPlus()
    {
        using var classifier = new OnnxSymbolClassifier(ModelDir);
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        Stroke horizontal = Line(new[] { (10.0, 20.0), (15, 20), (20, 20), (25, 20), (30, 20) });
        Stroke vertical = Line(new[] { (20.0, 10.0), (20, 15), (20, 20), (20, 25), (20, 30) });

        RecognitionResult result = recognizer.Recognize(new[] { horizontal, vertical });

        Assert.Equal("+", result.Latex);
        Assert.True(result.Confidence > 0.5, $"confidence was {result.Confidence}");
        Assert.Single(result.Tokens);
        Assert.Equal(2, result.Tokens[0].SourceStrokeIds.Count);   // both strokes form the '+'
    }

    [Fact]
    public void RealModel_SegmentsAndReadsTwoSymbols()
    {
        using var classifier = new OnnxSymbolClassifier(ModelDir);
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        // Two '+' symbols, well separated horizontally → the real multi-symbol pipeline.
        Stroke h1 = Line(new[] { (0.0, 20.0), (5, 20), (10, 20), (15, 20), (20, 20) });
        Stroke v1 = Line(new[] { (10.0, 10.0), (10, 15), (10, 20), (10, 25), (10, 30) });
        Stroke h2 = Line(new[] { (60.0, 20.0), (65, 20), (70, 20), (75, 20), (80, 20) });
        Stroke v2 = Line(new[] { (70.0, 10.0), (70, 15), (70, 20), (70, 25), (70, 30) });

        RecognitionResult result = recognizer.Recognize(new[] { h1, v1, h2, v2 });

        Assert.Equal("++", result.Latex);
        Assert.Equal(2, result.Tokens.Count);
    }

    private sealed class FakeClassifier : ISymbolClassifier
    {
        private readonly Func<InkBounds, string> _label;
        public FakeClassifier(Func<InkBounds, string> label) => _label = label;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            new(_label(SymbolPreprocessor.Bounds(strokes)), 1.0);
    }

    // Records the context handed to each symbol and returns scripted (label, confidence) pairs in
    // call order — which is the segmenter's left-to-right group order.
    private sealed class SpyClassifier : ISymbolClassifier
    {
        private readonly (string Label, double Confidence)[] _returns;
        private int _index;
        public SpyClassifier(params (string Label, double Confidence)[] returns) => _returns = returns;

        public List<SymbolContext> Contexts { get; } = new();

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            Contexts.Add(context);
            (string label, double confidence) = _returns[Math.Min(_index, _returns.Length - 1)];
            _index++;
            return new SymbolPrediction(label, confidence);
        }
    }

    private static Stroke VLine(double x) => Line(Enumerable.Range(0, 11).Select(i => (x, i * 2.0)));
    private static Stroke HLine(double x0, double y) => Line(Enumerable.Range(0, 11).Select(i => (x0 + i * 2.0, y)));
    private static Stroke VBar(double x, double height) => Line(Enumerable.Range(0, 11).Select(i => (x, height * i / 10.0)));

    private static Stroke Line(IEnumerable<(double X, double Y)> points) =>
        new(Guid.NewGuid(), points.Select(p => new StrokeSample(p.X, p.Y, TimeSpan.Zero, 0.5)).ToList());
}
