using Penumbra.Core;

namespace Penumbra.ExpressionCorpus;

public interface IExpressionScenarioRuntimeFactory
{
    string PipelineFingerprint { get; }

    string ModelFingerprint { get; }

    double RecognitionThreshold { get; }

    IExpressionScenarioRuntime Create(
        ExpressionScenarioInputV1 input,
        ILocalMetricsSink metrics);
}

public interface IExpressionScenarioRuntime : IAsyncDisposable
{
    Task<StepActualV1> ApplyAsync(
        ScenarioActionV1 action,
        CancellationToken cancellationToken);
}

internal sealed class CountingBoundedMetricsSink : ILocalMetricsSink
{
    private readonly object _gate = new();
    private readonly BoundedInMemoryMetricsSink _inner;
    private long _totalCount;
    private bool _sealed;

    public CountingBoundedMetricsSink(int capacity)
    {
        _inner = new BoundedInMemoryMetricsSink(capacity);
    }

    public int Capacity => _inner.Capacity;

    public void Record(MetricObservation observation)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                return;
            }
            _totalCount++;
            _inner.Record(observation);
        }
    }

    public (long TotalCount, LocalMetricsSnapshot Snapshot) Capture()
    {
        lock (_gate)
        {
            return (_totalCount, _inner.Snapshot());
        }
    }

    public (long TotalCount, LocalMetricsSnapshot Snapshot) SealAndCapture()
    {
        lock (_gate)
        {
            _sealed = true;
            return (_totalCount, _inner.Snapshot());
        }
    }
}
