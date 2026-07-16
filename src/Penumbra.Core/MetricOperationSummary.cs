namespace Penumbra.Core;

/// <summary>
/// Deterministic counts and completed-only latency percentiles for one fixed operation. Percentiles use
/// nearest rank: sort completed durations, select the 1-based rank <c>ceil(p * sampleCount / 100)</c>.
/// </summary>
public readonly record struct MetricOperationSummary
{
    internal MetricOperationSummary(
        MetricOperation operation,
        int sampleCount,
        int completedCount,
        int cancelledCount,
        int refusedCount,
        int failedCount,
        TimeSpan? completedDurationP50,
        TimeSpan? completedDurationP95)
    {
        Operation = operation;
        SampleCount = sampleCount;
        CompletedCount = completedCount;
        CancelledCount = cancelledCount;
        RefusedCount = refusedCount;
        FailedCount = failedCount;
        CompletedDurationP50 = completedDurationP50;
        CompletedDurationP95 = completedDurationP95;
    }

    /// <summary>The fixed operation summarized.</summary>
    public MetricOperation Operation { get; }

    /// <summary>All retained observations for the operation.</summary>
    public int SampleCount { get; }

    /// <summary>Retained observations that completed successfully.</summary>
    public int CompletedCount { get; }

    /// <summary>Retained observations cancelled before completion.</summary>
    public int CancelledCount { get; }

    /// <summary>Retained observations deliberately refused.</summary>
    public int RefusedCount { get; }

    /// <summary>Retained observations that failed.</summary>
    public int FailedCount { get; }

    /// <summary>Completed-only p50 duration by nearest-rank semantics, or null when none completed.</summary>
    public TimeSpan? CompletedDurationP50 { get; }

    /// <summary>Completed-only p95 duration by nearest-rank semantics, or null when none completed.</summary>
    public TimeSpan? CompletedDurationP95 { get; }
}
