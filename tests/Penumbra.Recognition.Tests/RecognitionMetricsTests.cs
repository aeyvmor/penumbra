using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Pins Recognition's local metrics contract without weakening the existing direct-construction,
/// cancellation, result, or exception seams.
/// </summary>
public sealed class RecognitionMetricsTests
{
    [Fact]
    public void LegacyConstructors_RemainNoOpCompatible()
    {
        var segmenter = new OverlapStrokeSegmenter();
        var classifier = new ConstantClassifier("7");
        Stroke[] strokes = { Bar(0, 0), Bar(40, 0) };

        var pageRecognizer = new ExpressionRecognizer(segmenter, classifier);
        var regionAwareRecognizer = new ExpressionRecognizer(
            segmenter,
            new RegionSegmenter(segmenter),
            classifier);

        Assert.Equal("77", pageRecognizer.Recognize(strokes).Latex);
        Assert.Equal("77", regionAwareRecognizer.Recognize(strokes).Latex);
    }

    [Fact]
    public void NoOpSink_DoesNotReadTheInjectedClock()
    {
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("7"),
            NoOpLocalMetricsSink.Instance,
            new ThrowingTimeProvider());

        RecognitionResult result = recognizer.Recognize(new[] { Bar(0, 0) });

        Assert.Equal("7", result.Latex);
    }

    [Fact]
    public void EmptyPage_IsCompletedZeroWork_NotARefusal()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("7"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        RecognitionResult result = recognizer.Recognize(Array.Empty<Stroke>());

        Assert.Empty(result.Tokens);
        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndCount(
                observation, MetricOperation.RecognitionPartition, 0),
            observation => AssertOperationAndCount(
                observation, MetricOperation.RecognitionProcessing, 0));
        Assert.DoesNotContain(
            sink.Snapshot().Observations,
            observation => observation.Outcome == MetricOutcome.Refused);
    }

    [Fact]
    public void RejectedPrediction_IsCompletedPipelineWork_NotARefusal()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new RejectedClassifier(),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        RecognitionResult result = recognizer.Recognize(new[] { Bar(0, 0) });

        Assert.True(Assert.Single(result.Tokens).Rejected);
        Assert.All(
            sink.Snapshot().Observations,
            observation => Assert.Equal(MetricOutcome.Completed, observation.Outcome));
    }

    [Fact]
    public void PageRecognition_EmitsEachCompletedOperationOnce_WithExactCountsAndDurations()
    {
        var sink = new BoundedInMemoryMetricsSink(16);
        var time = new StepTimeProvider(TimeSpan.FromMilliseconds(10));
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("7"),
            sink,
            time);

        RecognitionResult result = recognizer.Recognize(new[] { Bar(0, 0), Bar(40, 0) });

        Assert.Equal("77", result.Latex);
        Assert.Equal(8, time.ReadCount);
        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertObservation(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Completed, 1, 10),
            observation => AssertObservation(
                observation, MetricOperation.RecognitionClassification, MetricOutcome.Completed, 2, 10),
            observation => AssertObservation(
                observation, MetricOperation.RecognitionGrammar, MetricOutcome.Completed, 2, 10),
            observation => AssertObservation(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Completed, 1, 70));
    }

    [Fact]
    public void PrePartitionedRegion_EmitsNoPartitionObservation()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        Stroke stroke = Bar(0, 0);
        var bounds = new InkBounds(0, 0, 1, 20);
        var group = new StrokeGroup(new[] { stroke }, bounds);
        var region = new InkRegion(Guid.NewGuid(), new[] { stroke.Id }, bounds, new[] { group });
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("7"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        RecognitionResult result = recognizer.RecognizeRegion(region);

        Assert.Equal("7", result.Latex);
        Assert.Equal(
            new[]
            {
                MetricOperation.RecognitionClassification,
                MetricOperation.RecognitionGrammar,
                MetricOperation.RecognitionProcessing,
            },
            sink.Snapshot().Observations.Select(observation => observation.Operation));
    }

    [Fact]
    public void IncrementalRecognition_CountsDirtyRegions_AndDoesNotObserveReusedStages()
    {
        var sink = new BoundedInMemoryMetricsSink(24);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("x"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));
        Stroke[] strokes = { Bar(0, 0), Bar(0, 300) };

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(strokes);

        Assert.Equal(2, first.Count);
        Assert.All(first, region => Assert.True(region.Dirty));
        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionPartition, 2),
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionClassification, 1),
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionGrammar, 1),
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionClassification, 1),
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionGrammar, 1),
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionProcessing, 2));

        IReadOnlyList<RegionRecognition> second = recognizer.RecognizeRegions(strokes, first);

        Assert.All(second, region => Assert.False(region.Dirty));
        MetricObservation[] secondPass = sink.Snapshot().Observations.Skip(6).ToArray();
        Assert.Collection(
            secondPass,
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionPartition, 2),
            observation => AssertOperationAndCount(observation, MetricOperation.RecognitionProcessing, 0));
    }

    [Fact]
    public async Task PreCancelledAsyncRecognition_DoesNotStartAClockBeforeScheduledWork()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var time = new StepTimeProvider(TimeSpan.FromMilliseconds(1));
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("7"),
            sink,
            time);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recognizer.RecognizeAsync(new[] { Bar(0, 0) }, cancellation.Token));

        Assert.Equal(0, time.ReadCount);
        Assert.Empty(sink.Snapshot().Observations);
    }

    [Fact]
    public async Task RequestedCancellation_RecordsOnlyCancelledActiveScopes_AndRethrows()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sink = new BoundedInMemoryMetricsSink(8);
        var classifier = new BlockingClassifier(entered, release);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            classifier,
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));
        using var cancellation = new CancellationTokenSource();

        Task<RecognitionResult> pending = recognizer.RecognizeAsync(
            new[] { Bar(0, 0) }, cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Completed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionClassification, MetricOutcome.Cancelled),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Cancelled));
        MetricOperationSummary processing = sink.Snapshot()
            .SummaryFor(MetricOperation.RecognitionProcessing);
        Assert.Equal(0, processing.CompletedCount);
        Assert.Equal(1, processing.CancelledCount);
        Assert.Null(processing.CompletedDurationP50);
        Assert.Null(processing.CompletedDurationP95);
    }

    [Fact]
    public async Task CancellationRaisedDuringEmptyPagePartition_IsNotRecordedAsCompletedZeroWork()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new BoundedInMemoryMetricsSink(8);
        var recognizer = new ExpressionRecognizer(
            new CancellingSegmenter(cancellation),
            new ConstantClassifier("7"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recognizer.RecognizeAsync(Array.Empty<Stroke>(), cancellation.Token));

        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Cancelled),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Cancelled));
    }

    [Fact]
    public void CancellationRaisedDuringEmptyRegionPartition_IsNotRecordedAsCompletedZeroWork()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new BoundedInMemoryMetricsSink(8);
        var recognizer = new ExpressionRecognizer(
            new CancellingSegmenter(cancellation),
            new ConstantClassifier("7"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            recognizer.RecognizeRegions(Array.Empty<Stroke>(), cancellationToken: cancellation.Token));

        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Cancelled),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Cancelled));
    }

    [Fact]
    public void PreCancelledEmptyPrePartitionedRegion_IsRecordedAsCancelledNotCompleted()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sink = new BoundedInMemoryMetricsSink(8);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ConstantClassifier("7"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));
        var empty = new InkRegion(
            Guid.NewGuid(),
            Array.Empty<Guid>(),
            new InkBounds(0, 0, 0, 0),
            Array.Empty<StrokeGroup>());

        Assert.ThrowsAny<OperationCanceledException>(() =>
            recognizer.RecognizeRegion(empty, cancellation.Token));

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        AssertOperationAndOutcome(
            observation, MetricOperation.RecognitionProcessing, MetricOutcome.Cancelled);
        Assert.Null(observation.ItemCount);
    }

    [Fact]
    public void ClassifierFailure_RecordsFailedActiveScopes_AndRethrowsSameException()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var expected = new InvalidOperationException("injected classifier failure");
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ThrowingClassifier(expected),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            recognizer.Recognize(new[] { Bar(0, 0) }));

        Assert.Same(expected, actual);
        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Completed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionClassification, MetricOutcome.Failed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Failed));
    }

    [Fact]
    public void UnrequestedOperationCanceledException_IsFailureNotCallerCancellation()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var expected = new OperationCanceledException("classifier aborted independently");
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new ThrowingClassifier(expected),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(() =>
            recognizer.Recognize(new[] { Bar(0, 0) }));

        Assert.Same(expected, actual);
        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Completed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionClassification, MetricOutcome.Failed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Failed));
    }

    [Fact]
    public void PartitionFailure_RecordsOnlyFailedPartitionAndProcessing_AndRethrows()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var expected = new InvalidOperationException("injected partition failure");
        var recognizer = new ExpressionRecognizer(
            new ThrowingSegmenter(expected),
            new ConstantClassifier("7"),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() =>
            recognizer.Recognize(new[] { Bar(0, 0) }));

        Assert.Same(expected, actual);
        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Failed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Failed));
    }

    [Fact]
    public void MalformedBatchFailure_SeparatesCompletedClassificationFromFailedGrammar()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            new EmptyBatchClassifier(),
            sink,
            new StepTimeProvider(TimeSpan.FromMilliseconds(1)));

        // Phase 5.5 slice 4: the grammar stage now indexes `predictions[i]` directly (building the raw
        // Seam-1 token list feeding SpatialLayoutParser) instead of first copying into a `string[]` labels
        // array — a malformed classifier that under-returns predictions still crashes the grammar stage
        // (proving the metrics separation below), just via ArgumentOutOfRangeException (interface-typed
        // IReadOnlyList<T> indexing) rather than the old raw-array IndexOutOfRangeException.
        Assert.Throws<ArgumentOutOfRangeException>(() => recognizer.Recognize(new[] { Bar(0, 0) }));

        Assert.Collection(
            sink.Snapshot().Observations,
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionPartition, MetricOutcome.Completed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionClassification, MetricOutcome.Completed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionGrammar, MetricOutcome.Failed),
            observation => AssertOperationAndOutcome(
                observation, MetricOperation.RecognitionProcessing, MetricOutcome.Failed));
    }

    private static void AssertObservation(
        MetricObservation observation,
        MetricOperation operation,
        MetricOutcome outcome,
        int itemCount,
        int milliseconds)
    {
        Assert.Equal(operation, observation.Operation);
        Assert.Equal(outcome, observation.Outcome);
        Assert.Equal(itemCount, observation.ItemCount);
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), observation.Duration);
    }

    private static void AssertOperationAndCount(
        MetricObservation observation, MetricOperation operation, int itemCount)
    {
        AssertOperationAndOutcome(observation, operation, MetricOutcome.Completed);
        Assert.Equal(itemCount, observation.ItemCount);
    }

    private static void AssertOperationAndOutcome(
        MetricObservation observation, MetricOperation operation, MetricOutcome outcome)
    {
        Assert.Equal(operation, observation.Operation);
        Assert.Equal(outcome, observation.Outcome);
    }

    private sealed class ConstantClassifier : ISymbolClassifier
    {
        private readonly string _label;

        public ConstantClassifier(string label) => _label = label;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            new(_label, 1.0);
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
                throw new TimeoutException("Test did not release the classifier.");
            }

            return new SymbolPrediction("7", 1.0);
        }
    }

    private sealed class RejectedClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            new("?", 0.1, Rejected: true);
    }

    private sealed class ThrowingClassifier : ISymbolClassifier
    {
        private readonly Exception _exception;

        public ThrowingClassifier(Exception exception) => _exception = exception;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            throw _exception;
    }

    private sealed class ThrowingSegmenter : IStrokeSegmenter
    {
        private readonly Exception _exception;

        public ThrowingSegmenter(Exception exception) => _exception = exception;

        public IReadOnlyList<StrokeGroup> Segment(IReadOnlyList<Stroke> strokes) =>
            throw _exception;
    }

    private sealed class CancellingSegmenter(CancellationTokenSource cancellation) : IStrokeSegmenter
    {
        public IReadOnlyList<StrokeGroup> Segment(IReadOnlyList<Stroke> strokes)
        {
            cancellation.Cancel();
            return Array.Empty<StrokeGroup>();
        }
    }

    private sealed class EmptyBatchClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            throw new NotSupportedException("The test exercises the batch seam.");

        public IReadOnlyList<SymbolPrediction> ClassifyBatch(
            IReadOnlyList<IReadOnlyList<Stroke>> symbols, SymbolContext context) =>
            Array.Empty<SymbolPrediction>();
    }

    private sealed class StepTimeProvider : TimeProvider
    {
        private readonly long _step;
        private long _next;
        private int _readCount;

        public StepTimeProvider(TimeSpan step) => _step = step.Ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public int ReadCount => Volatile.Read(ref _readCount);

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _readCount);
            return Interlocked.Add(ref _next, _step) - _step;
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override long GetTimestamp() =>
            throw new InvalidOperationException("The no-op path must not read a timestamp.");
    }

    private static Stroke Bar(double x, double y) =>
        new(Guid.NewGuid(), new[]
        {
            new StrokeSample(x, y, TimeSpan.Zero, 0.5),
            new StrokeSample(x, y + 20, TimeSpan.Zero, 0.5),
        });
}
