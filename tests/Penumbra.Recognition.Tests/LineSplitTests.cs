using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 3.9f: the Y-projection line-split guard. The page is cut into horizontal lines and only
/// ONE line is recognized, so a stray mark far above/below the writing can't pollute the geometry
/// context (ref_h / expr_ymin / expr_h) that every symbol on the real line is judged against. A
/// recording classifier isolates which groups get classified and the exact context they receive.
/// </summary>
public sealed class LineSplitTests
{
    [Fact]
    public void StrayMarkOnAnotherLineIsNotClassified()
    {
        // Main line: three symbols near the top. Stray: a lone mark far below — its own "line".
        var main = new[] { VStroke(0, 0, 10), VStroke(40, 0, 20), VStroke(80, 0, 30) };
        Stroke stray = VStroke(200, 500, 100);

        var classifier = new RecordingClassifier(b => b.X < 20 ? "1" : b.X < 60 ? "2" : b.X < 120 ? "3" : "S");
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        RecognitionResult result = recognizer.Recognize(main.Append(stray).ToList());

        // Only the 3-symbol line is recognized; the stray contributes no token and is never classified.
        Assert.Equal("123", result.Latex);
        Assert.Equal(3, result.Tokens.Count);
        Assert.DoesNotContain(classifier.Seen, b => b.X > 150);   // the stray at x=200 was never seen
    }

    [Fact]
    public void SingleLineIsRecognizedUnchanged()
    {
        var strokes = new[] { VStroke(0, 0, 20), VStroke(40, 0, 20), VStroke(80, 0, 20) };
        var classifier = new RecordingClassifier(b => b.X < 20 ? "a" : b.X < 60 ? "b" : "c");
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        RecognitionResult result = recognizer.Recognize(strokes);

        Assert.Equal("abc", result.Latex);
        Assert.Equal(3, result.Tokens.Count);
    }

    [Fact]
    public void LineContextReflectsOnlyTheSelectedLine_NotStrayMarks()
    {
        var main = new[] { VStroke(0, 0, 10), VStroke(40, 0, 20), VStroke(80, 0, 30) };
        Stroke stray = VStroke(200, 500, 100);

        var classifier = new RecordingClassifier(_ => "x");
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        recognizer.Recognize(main.Append(stray).ToList());

        // Context is built from the selected line's bounds ONLY: median height 20 over y∈[0,30].
        // Folding in the stray (height 100 at y=500) would give RefHeight 25 and ExprHeight 600.
        Assert.Equal(3, classifier.Contexts.Count);
        SymbolContext ctx = classifier.Contexts[0];
        Assert.All(classifier.Contexts, c => Assert.Equal(ctx, c));
        Assert.Equal(20.0, ctx.RefHeight, 3);
        Assert.Equal(0.0, ctx.ExprYMin, 3);
        Assert.Equal(30.0, ctx.ExprHeight, 3);
    }

    [Fact]
    public void EqualSizedLines_TieBreakByWiderXExtent()
    {
        // Two lines of two symbols each; the lower line is wider, so the tie-break selects it.
        var narrow = new[] { VStroke(0, 0, 20), VStroke(30, 0, 20) };       // x-extent 30
        var wide = new[] { VStroke(0, 200, 20), VStroke(300, 200, 20) };    // x-extent 300

        var classifier = new RecordingClassifier(b => b.Y < 100 ? "n" : "w");
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        RecognitionResult result = recognizer.Recognize(narrow.Concat(wide).ToList());

        Assert.Equal("ww", result.Latex);
    }

    // Captures every symbol's bounds and the context it was classified against, in call order.
    private sealed class RecordingClassifier : ISymbolClassifier
    {
        private readonly Func<InkBounds, string> _label;
        public RecordingClassifier(Func<InkBounds, string> label) => _label = label;

        public List<InkBounds> Seen { get; } = new();

        public List<SymbolContext> Contexts { get; } = new();

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            InkBounds box = SymbolPreprocessor.Bounds(strokes);
            Seen.Add(box);
            Contexts.Add(context);
            return new SymbolPrediction(_label(box), 1.0);
        }
    }

    // A vertical stroke at column x spanning [y0, y0+height].
    private static Stroke VStroke(double x, double y0, double height) =>
        new(Guid.NewGuid(), Enumerable.Range(0, 11)
            .Select(i => new StrokeSample(x, y0 + height * i / 10.0, TimeSpan.Zero, 0.5))
            .ToList());
}
