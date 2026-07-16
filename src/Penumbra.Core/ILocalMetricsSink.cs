namespace Penumbra.Core;

/// <summary>Receives bounded, numeric-only observations for local diagnostics.</summary>
public interface ILocalMetricsSink
{
    /// <summary>Records one terminal operation observation.</summary>
    void Record(MetricObservation observation);
}
