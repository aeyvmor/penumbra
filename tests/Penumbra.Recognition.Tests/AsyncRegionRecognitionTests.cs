using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>Contract tests for the Increment-2 asynchronous region-recognition seam.</summary>
public sealed class AsyncRegionRecognitionTests
{
    [Fact]
    public async Task AsyncAndSyncPathsAreExactlyEquivalent()
    {
        Stroke[] strokes = { Bar(0, 0), Bar(40, 0), Bar(0, 300) };
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), new CountingClassifier());

        IReadOnlyList<RegionRecognition> sync = recognizer.RecognizeRegions(strokes);
        IReadOnlyList<RegionRecognition> asyncResult = await recognizer.RecognizeRegionsAsync(strokes);

        Assert.Equal(sync.Count, asyncResult.Count);
        for (var i = 0; i < sync.Count; i++)
        {
            Assert.Equal(sync[i].Region.StrokeIds, asyncResult[i].Region.StrokeIds);
            Assert.Equal(sync[i].Result.Latex, asyncResult[i].Result.Latex);
            Assert.Equal(sync[i].Result.Confidence, asyncResult[i].Result.Confidence);
            Assert.Equal(sync[i].Result.MinConfidence, asyncResult[i].Result.MinConfidence);
            Assert.Equal(sync[i].Result.Tokens.Count, asyncResult[i].Result.Tokens.Count);
            for (var tokenIndex = 0; tokenIndex < sync[i].Result.Tokens.Count; tokenIndex++)
            {
                RecognizedToken expected = sync[i].Result.Tokens[tokenIndex];
                RecognizedToken actual = asyncResult[i].Result.Tokens[tokenIndex];
                Assert.Equal(expected.Latex, actual.Latex);
                Assert.Equal(expected.SourceStrokeIds, actual.SourceStrokeIds);
                Assert.Equal(expected.Bounds, actual.Bounds);
                Assert.Equal(expected.Confidence, actual.Confidence);
                Assert.Equal(expected.Rejected, actual.Rejected);
            }
            Assert.Equal(sync[i].Dirty, asyncResult[i].Dirty);
        }
    }

    [Fact]
    public async Task PreviousRoundTripReusesCleanResultAndClassifiesOnlyDirtyRegion()
    {
        var firstLine = new List<Stroke> { Bar(0, 0), Bar(40, 0) };
        var secondLine = new List<Stroke> { Bar(0, 300), Bar(40, 300) };
        var classifier = new CountingClassifier();
        IRegionRecognizer recognizer =
            new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        IReadOnlyList<RegionRecognition> first = await recognizer.RecognizeRegionsAsync(
            firstLine.Concat(secondLine).ToList());
        int initialCalls = classifier.Calls;
        secondLine.Add(Bar(80, 300));

        IReadOnlyList<RegionRecognition> second = await recognizer.RecognizeRegionsAsync(
            firstLine.Concat(secondLine).ToList(), first);

        Assert.False(second[0].Dirty);
        Assert.Same(first[0].Result, second[0].Result);
        Assert.True(second[1].Dirty);
        Assert.Equal(initialCalls + 3, classifier.Calls);
    }

    [Fact]
    public async Task PreCancelledAsyncPassNeverSegmentsOrClassifies()
    {
        var classifier = new CountingClassifier();
        IRegionRecognizer recognizer =
            new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recognizer.RecognizeRegionsAsync(new[] { Bar(0, 0) }, cancellationToken: cts.Token));

        Assert.Equal(0, classifier.Calls);
    }

    [Fact]
    public async Task CancellationDuringDirtyRegionInferenceReturnsNoPartialRoundTrip()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var classifier = new BlockingClassifier(entered, release);
        IRegionRecognizer recognizer =
            new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);
        using var cts = new CancellationTokenSource();

        Task<IReadOnlyList<RegionRecognition>> pass = recognizer.RecognizeRegionsAsync(
            new[] { Bar(0, 0), Bar(0, 300) }, cancellationToken: cts.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cts.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
    }

    private sealed class CountingClassifier : ISymbolClassifier
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            Interlocked.Increment(ref _calls);
            return new SymbolPrediction("x", 1.0);
        }
    }

    private sealed class BlockingClassifier : ISymbolClassifier
    {
        private readonly ManualResetEventSlim _entered;
        private readonly ManualResetEventSlim _release;

        public BlockingClassifier(ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            _entered = entered;
            _release = release;
        }

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release classifier.");
            }

            return new SymbolPrediction("x", 1.0);
        }
    }

    private static Stroke Bar(double x, double y) =>
        new(Guid.NewGuid(), new[]
        {
            new StrokeSample(x, y, TimeSpan.Zero, 0.5),
            new StrokeSample(x, y + 20, TimeSpan.Zero, 0.5),
        });
}
