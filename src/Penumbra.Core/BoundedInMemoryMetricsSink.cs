namespace Penumbra.Core;

/// <summary>
/// Thread-safe local sink retaining at most <see cref="Capacity"/> observations. When full, recording
/// evicts the globally oldest observation regardless of operation or outcome.
/// </summary>
public sealed class BoundedInMemoryMetricsSink : ILocalMetricsSink
{
    private readonly object _gate = new();
    private readonly Queue<MetricObservation> _observations;

    public BoundedInMemoryMetricsSink(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be positive");
        }

        Capacity = capacity;
        _observations = new Queue<MetricObservation>(Math.Min(capacity, 4_096));
    }

    /// <summary>The hard maximum number of observations retained.</summary>
    public int Capacity { get; }

    /// <inheritdoc />
    public void Record(MetricObservation observation)
    {
        lock (_gate)
        {
            if (_observations.Count == Capacity)
            {
                _observations.Dequeue();
            }

            _observations.Enqueue(observation);
        }
    }

    /// <summary>Captures an immutable, deterministic view linearized with concurrent writers.</summary>
    public LocalMetricsSnapshot Snapshot()
    {
        MetricObservation[] observations;
        lock (_gate)
        {
            observations = _observations.ToArray();
        }

        return new LocalMetricsSnapshot(observations);
    }
}
