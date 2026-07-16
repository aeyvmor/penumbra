namespace Penumbra.Core;

/// <summary>
/// Centralized monotonic timer that records exactly one terminal observation. An unterminated scope is
/// cancelled on disposal so abandoned work never enters completed latency percentiles.
/// </summary>
public sealed class MetricTimingScope : IDisposable
{
    private static readonly MetricTimingScope NoOpScope = new();

    private readonly ILocalMetricsSink _sink = NoOpLocalMetricsSink.Instance;
    private readonly MetricOperation _operation;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly long _startedAt;
    private readonly object _terminalGate = new();
    private readonly bool _isNoOp = true;
    private bool _terminal;

    private MetricTimingScope()
    {
    }

    private MetricTimingScope(
        ILocalMetricsSink sink,
        MetricOperation operation,
        TimeProvider timeProvider)
    {
        _sink = sink;
        _operation = operation;
        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetTimestamp();
        _isNoOp = false;
    }

    /// <summary>Starts a scope on the system monotonic clock.</summary>
    public static MetricTimingScope Start(ILocalMetricsSink sink, MetricOperation operation) =>
        Start(sink, operation, TimeProvider.System);

    /// <summary>Starts a scope on an injected monotonic clock.</summary>
    public static MetricTimingScope Start(
        ILocalMetricsSink sink,
        MetricOperation operation,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        MetricValidation.ValidateOperation(operation);

        if (ReferenceEquals(sink, NoOpLocalMetricsSink.Instance))
        {
            return NoOpScope;
        }

        return new MetricTimingScope(sink, operation, timeProvider);
    }

    /// <summary>Records successful completion if this is the first terminal call.</summary>
    public void Complete(int? itemCount = null) => Finish(MetricOutcome.Completed, itemCount);

    /// <summary>Records cancellation if this is the first terminal call.</summary>
    public void Cancel(int? itemCount = null) => Finish(MetricOutcome.Cancelled, itemCount);

    /// <summary>Records deliberate refusal if this is the first terminal call.</summary>
    public void Refuse(int? itemCount = null) => Finish(MetricOutcome.Refused, itemCount);

    /// <summary>Records failure if this is the first terminal call.</summary>
    public void Fail(int? itemCount = null) => Finish(MetricOutcome.Failed, itemCount);

    /// <summary>Records cancellation only when no explicit terminal call has won.</summary>
    public void Dispose() => Finish(MetricOutcome.Cancelled, null);

    private void Finish(MetricOutcome outcome, int? itemCount)
    {
        MetricValidation.ValidateItemCount(itemCount);
        if (_isNoOp)
        {
            return;
        }

        MetricObservation observation;
        lock (_terminalGate)
        {
            if (_terminal)
            {
                return;
            }

            TimeSpan duration = _timeProvider.GetElapsedTime(_startedAt, _timeProvider.GetTimestamp());
            observation = new MetricObservation(_operation, outcome, duration, itemCount);
            _terminal = true;
        }

        _sink.Record(observation);
    }
}
