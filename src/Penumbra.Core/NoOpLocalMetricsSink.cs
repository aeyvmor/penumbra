namespace Penumbra.Core;

/// <summary>Singleton default sink that discards every observation without side effects.</summary>
public sealed class NoOpLocalMetricsSink : ILocalMetricsSink
{
    private NoOpLocalMetricsSink()
    {
    }

    /// <summary>The process-wide no-op sink.</summary>
    public static NoOpLocalMetricsSink Instance { get; } = new();

    /// <inheritdoc />
    public void Record(MetricObservation observation)
    {
    }
}
