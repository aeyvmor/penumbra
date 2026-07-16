namespace Penumbra.Core;

/// <summary>
/// One immutable local observation. Its fixed enums and numeric values deliberately leave no field for
/// expressions, paths, ink, identifiers, or other high-cardinality/user-bearing data.
/// </summary>
public readonly record struct MetricObservation
{
    public MetricObservation(
        MetricOperation operation,
        MetricOutcome outcome,
        TimeSpan duration,
        int? itemCount = null)
    {
        MetricValidation.ValidateOperation(operation);
        MetricValidation.ValidateOutcome(outcome);
        MetricValidation.ValidateDuration(duration);
        MetricValidation.ValidateItemCount(itemCount);

        Operation = operation;
        Outcome = outcome;
        Duration = duration;
        ItemCount = itemCount;
    }

    /// <summary>The fixed operation that ended.</summary>
    public MetricOperation Operation { get; }

    /// <summary>How the operation ended.</summary>
    public MetricOutcome Outcome { get; }

    /// <summary>Elapsed time measured by the caller's monotonic clock.</summary>
    public TimeSpan Duration { get; }

    /// <summary>An optional non-negative work-item count, such as affected Sheet nodes.</summary>
    public int? ItemCount { get; }
}

internal static class MetricValidation
{
    public static void ValidateOperation(MetricOperation operation)
    {
        if ((uint)operation > (uint)MetricOperation.GraphSampling)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "operation must be a defined metric operation");
        }
    }

    public static void ValidateOutcome(MetricOutcome outcome)
    {
        if ((uint)outcome > (uint)MetricOutcome.Failed)
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "outcome must be a defined metric outcome");
        }
    }

    public static void ValidateDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "duration must not be negative");
        }
    }

    public static void ValidateItemCount(int? itemCount)
    {
        if (itemCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "item count must not be negative");
        }
    }
}
