using Penumbra.Cas;
using Penumbra.Core;

namespace Penumbra.Sheet.Tests;

/// <summary>Proves Sheet metrics preserve graph semantics while reporting exact terminal work.</summary>
public sealed class SheetGraphMetricsTests
{
    [Fact]
    public void LegacyConstructionKeepsTheNoOpDefault()
    {
        var graph = new SheetGraph(new FakeEvaluator(), new FakeAnalyzer());
        graph.Upsert(Guid.NewGuid(), "x=5");

        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Single(report.CausallyAffectedNodes);
    }

    [Fact]
    public void RecomputeRecordsCausallyAffectedCount_NotChangedResultCount()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var evaluator = new AdvancingEvaluator(new AngouriMathEvaluator(), time, TimeSpan.FromMilliseconds(7));
        var graph = new SheetGraph(evaluator, new AngouriMathExpressionAnalyzer(), sink, time);
        Guid xId = Guid.NewGuid();
        graph.Upsert(xId, "x=5");
        graph.Upsert(Guid.NewGuid(), "y=x+2");
        graph.Upsert(Guid.NewGuid(), "y+1=");
        graph.RecomputeDetailed();

        graph.Upsert(xId, "x=2+3");
        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Empty(report.ChangedResultNodes);
        Assert.Equal(3, report.CausallyAffectedNodes.Count);
        MetricObservation[] observations = ObservationsFor(sink, MetricOperation.SheetRecompute);
        Assert.Equal(2, observations.Length);
        Assert.All(observations, observation => Assert.Equal(MetricOutcome.Completed, observation.Outcome));
        Assert.All(observations, observation => Assert.Equal(3, observation.ItemCount));
        Assert.All(observations, observation => Assert.Equal(TimeSpan.FromMilliseconds(21), observation.Duration));
    }

    [Fact]
    public void ProbeRecordsCompletedAffectedEntryCountExactlyOnce()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var evaluator = new AdvancingEvaluator(new FakeEvaluator(), time, TimeSpan.FromMilliseconds(5));
        var graph = new SheetGraph(evaluator, new FakeAnalyzer(), sink, time);
        Guid xId = Guid.NewGuid();
        graph.Upsert(xId, "x=5");
        graph.Upsert(Guid.NewGuid(), "y=x");
        graph.Upsert(Guid.NewGuid(), "y=");
        graph.RecomputeDetailed();

        SheetProbeReport report = graph.Probe(xId, "x=9");

        Assert.Equal(3, report.Entries.Count);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.TaffyProbe));
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(report.Entries.Count, observation.ItemCount);
        Assert.Equal(TimeSpan.FromMilliseconds(15), observation.Duration);
    }

    [Fact]
    public void ProbeReturningCycleErrorsIsStillCompletedWork()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var graph = new SheetGraph(new FakeEvaluator(), new FakeAnalyzer(), sink, time);
        Guid aId = Guid.NewGuid();
        graph.Upsert(aId, "a=1");
        graph.Upsert(Guid.NewGuid(), "b=a");
        graph.RecomputeDetailed();

        SheetProbeReport report = graph.Probe(aId, "a=b");

        Assert.Equal(2, report.Entries.Count);
        Assert.All(report.Entries, entry => Assert.Equal(EvaluationKind.Error, entry.TrialResult.Kind));
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.TaffyProbe));
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(2, observation.ItemCount);
    }

    [Fact]
    public void EvaluatorErrorResultsRemainCompletedWork()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var error = new EvaluationResult(string.Empty, "unsupported", IsComputed: false, EvaluationKind.Error);
        var graph = new SheetGraph(new ReturningEvaluator(error), new FakeAnalyzer(), sink, time);
        Guid nodeId = Guid.NewGuid();
        graph.Upsert(nodeId, "x+1=");

        RecomputeReport recompute = graph.RecomputeDetailed();
        SheetProbeReport probe = graph.Probe(nodeId, "x+2=");

        Assert.Equal(EvaluationKind.Error, Assert.Single(recompute.CausallyAffectedNodes).Result!.Kind);
        Assert.Equal(EvaluationKind.Error, Assert.Single(probe.Entries).TrialResult.Kind);
        Assert.Equal(
            MetricOutcome.Completed,
            Assert.Single(ObservationsFor(sink, MetricOperation.SheetRecompute)).Outcome);
        Assert.Equal(
            MetricOutcome.Completed,
            Assert.Single(ObservationsFor(sink, MetricOperation.TaffyProbe)).Outcome);
    }

    [Fact]
    public void UnknownProbeRecordsFailedExactlyOnceAndRethrows()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var graph = new SheetGraph(new FakeEvaluator(), new FakeAnalyzer(), sink, time);

        Assert.Throws<ArgumentException>(() => graph.Probe(Guid.NewGuid(), "x=9"));

        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.TaffyProbe));
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
        Assert.Null(observation.ItemCount);
    }

    [Fact]
    public void EmbeddedCancellationWithoutCallerTokenRecordsFailedExactlyOnceAndRethrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var graph = new SheetGraph(
            new ThrowingEvaluator(new OperationCanceledException(cancellation.Token)),
            new FakeAnalyzer(),
            sink,
            time);
        graph.Upsert(Guid.NewGuid(), "1+1=");

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(graph.RecomputeDetailed);

        Assert.True(exception.CancellationToken.IsCancellationRequested);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.SheetRecompute));
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
        Assert.Null(observation.ItemCount);
    }

    [Fact]
    public void ProbeEmbeddedCancellationWithoutCallerTokenRecordsFailedExactlyOnceAndRethrows()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var graph = new SheetGraph(
            new ThrowingEvaluator(new OperationCanceledException(cancellation.Token)),
            new FakeAnalyzer(),
            sink,
            time);
        Guid nodeId = Guid.NewGuid();
        graph.Upsert(nodeId, "1+1=");

        OperationCanceledException exception = Assert.Throws<OperationCanceledException>(
            () => graph.Probe(nodeId, "1+2="));

        Assert.True(exception.CancellationToken.IsCancellationRequested);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.TaffyProbe));
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
        Assert.Null(observation.ItemCount);
    }

    [Fact]
    public void EvaluationExceptionRecordsFailedExactlyOnceAndRethrows()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var graph = new SheetGraph(
            new ThrowingEvaluator(new InvalidOperationException("boom")),
            new FakeAnalyzer(),
            sink,
            time);
        graph.Upsert(Guid.NewGuid(), "1+1=");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(graph.RecomputeDetailed);

        Assert.Equal("boom", exception.Message);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.SheetRecompute));
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
        Assert.Null(observation.ItemCount);
    }

    [Fact]
    public void UnrequestedCancellationExceptionRecordsFailed()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(8);
        var graph = new SheetGraph(
            new ThrowingEvaluator(new OperationCanceledException()),
            new FakeAnalyzer(),
            sink,
            time);
        graph.Upsert(Guid.NewGuid(), "1+1=");

        Assert.Throws<OperationCanceledException>(graph.RecomputeDetailed);

        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.SheetRecompute));
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
    }

    private static MetricObservation[] ObservationsFor(
        BoundedInMemoryMetricsSink sink,
        MetricOperation operation) =>
        sink.Snapshot().Observations.Where(observation => observation.Operation == operation).ToArray();

    private sealed class AdvancingEvaluator : IEvaluator
    {
        private readonly IEvaluator _inner;
        private readonly ManualTimestampTimeProvider _timeProvider;
        private readonly TimeSpan _advanceBy;

        public AdvancingEvaluator(
            IEvaluator inner,
            ManualTimestampTimeProvider timeProvider,
            TimeSpan advanceBy)
        {
            _inner = inner;
            _timeProvider = timeProvider;
            _advanceBy = advanceBy;
        }

        public EvaluationResult Evaluate(EvaluationRequest request)
        {
            _timeProvider.Advance(_advanceBy);
            return _inner.Evaluate(request);
        }
    }

    private sealed class ThrowingEvaluator : IEvaluator
    {
        private readonly Exception _exception;

        public ThrowingEvaluator(Exception exception) => _exception = exception;

        public EvaluationResult Evaluate(EvaluationRequest request) => throw _exception;
    }

    private sealed class ReturningEvaluator : IEvaluator
    {
        private readonly EvaluationResult _result;

        public ReturningEvaluator(EvaluationResult result) => _result = result;

        public EvaluationResult Evaluate(EvaluationRequest request) => _result;
    }

    private sealed class ManualTimestampTimeProvider : TimeProvider
    {
        private const long Frequency = 1_000;
        private long _timestamp;

        public override long TimestampFrequency => Frequency;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            long delta = checked(duration.Ticks * Frequency / TimeSpan.TicksPerSecond);
            _timestamp = checked(_timestamp + delta);
        }
    }
}
