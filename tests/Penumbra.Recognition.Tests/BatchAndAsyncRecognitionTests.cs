using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 4.5a: the batched ONNX path must be indistinguishable from the per-symbol path (the batch
/// axis is a throughput change, never a scoring change), and recognition must be awaitable and
/// cancellable so live mode can supersede stale reads.
/// </summary>
public sealed class BatchAndAsyncRecognitionTests
{
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "Models");

    [Fact]
    public void ClassifyBatch_OnRealModel_MatchesSingleCalls()
    {
        using var classifier = new OnnxSymbolClassifier(ModelDir);

        // Three distinct symbols the R1 model reads robustly: '+', '-', '1'.
        var plus = new IReadOnlyList<Stroke>[]
        {
            new[] { HSeg(10, 20, 20), VSeg(20, 10, 20) },   // +
            new[] { HSeg(50, 20, 20) },                     // -
            new[] { VSeg(100, 8, 24) },                     // 1 or |
        };
        SymbolContext context = new(RefHeight: 20, ExprYMin: 8, ExprHeight: 24);

        var single = plus.Select(s => classifier.Classify(s, context)).ToArray();
        IReadOnlyList<SymbolPrediction> batch = classifier.ClassifyBatch(plus, context);

        Assert.Equal(single.Length, batch.Count);
        for (int i = 0; i < single.Length; i++)
        {
            Assert.Equal(single[i].Label, batch[i].Label);
            Assert.True(Math.Abs(single[i].Confidence - batch[i].Confidence) < 1e-4,
                $"symbol {i}: single={single[i].Confidence} batch={batch[i].Confidence}");
        }
    }

    [Fact]
    public void ClassifyBatch_EmptyEntry_YieldsEmptyPrediction_OthersStillScored()
    {
        using var classifier = new OnnxSymbolClassifier(ModelDir);
        SymbolContext context = new(RefHeight: 20, ExprYMin: 10, ExprHeight: 20);

        IReadOnlyList<SymbolPrediction> batch = classifier.ClassifyBatch(
            new IReadOnlyList<Stroke>[]
            {
                Array.Empty<Stroke>(),
                new[] { HSeg(10, 20, 20), VSeg(20, 10, 20) },   // +
            },
            context);

        Assert.Equal(2, batch.Count);
        Assert.Equal(string.Empty, batch[0].Label);
        Assert.Equal(0, batch[0].Confidence);
        Assert.Equal("+", batch[1].Label);
        Assert.True(batch[1].Confidence > 0.5);
    }

    [Fact]
    public async Task RecognizeAsync_ReturnsSameResultAsSync()
    {
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), new ConstClassifier("7"));
        var strokes = new[] { VSeg(0, 0, 20), VSeg(60, 0, 20) };

        RecognitionResult sync = recognizer.Recognize(strokes);
        RecognitionResult async = await recognizer.RecognizeAsync(strokes);

        Assert.Equal(sync.Latex, async.Latex);
        Assert.Equal(sync.Tokens.Count, async.Tokens.Count);
        Assert.Equal(sync.Confidence, async.Confidence);
        Assert.Equal(sync.MinConfidence, async.MinConfidence);
    }

    [Fact]
    public async Task RecognizeAsync_AlreadyCancelled_Throws()
    {
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), new ConstClassifier("7"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => recognizer.RecognizeAsync(new[] { VSeg(0, 0, 20) }, cts.Token));
    }

    [Fact]
    public async Task RecognizeAsync_DefaultInterfaceBridge_WorksForSyncOnlyImplementations()
    {
        // A recognizer that only implements the sync method still gets a working async form —
        // that keeps NoOpRecognizer and test fakes honest members of the Phase 4.5 interface.
        IRecognizer recognizer = new NoOpRecognizer();

        RecognitionResult result = await recognizer.RecognizeAsync(Array.Empty<Stroke>());

        Assert.Equal(string.Empty, result.Latex);
    }

    /// <summary>Always returns the same label at full confidence; only implements the single-call form.</summary>
    private sealed class ConstClassifier : ISymbolClassifier
    {
        private readonly string _label;
        public ConstClassifier(string label) => _label = label;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            new(_label, 1.0);
    }

    private static Stroke HSeg(double x0, double y, double length) =>
        Line(Enumerable.Range(0, 11).Select(i => (x0 + length * i / 10.0, y)));

    private static Stroke VSeg(double x, double y0, double length) =>
        Line(Enumerable.Range(0, 11).Select(i => (x, y0 + length * i / 10.0)));

    private static Stroke Line(IEnumerable<(double X, double Y)> points) =>
        new(Guid.NewGuid(), points.Select(p => new StrokeSample(p.X, p.Y, TimeSpan.Zero, 0.5)).ToList());
}
