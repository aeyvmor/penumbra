using System.Collections.ObjectModel;

namespace Penumbra.Core;

/// <summary>
/// Immutable point-in-time view of retained observations and enum-ordered operation summaries.
/// Observations are oldest first; summaries include every operation, including those with zero samples.
/// </summary>
public sealed class LocalMetricsSnapshot
{
    private readonly ReadOnlyCollection<MetricObservation> _observations;
    private readonly ReadOnlyCollection<MetricOperationSummary> _summaries;

    internal LocalMetricsSnapshot(MetricObservation[] observations)
    {
        _observations = Array.AsReadOnly(observations);
        _summaries = Array.AsReadOnly(BuildSummaries(observations));
    }

    /// <summary>The exact retained observations, ordered from oldest to newest.</summary>
    public IReadOnlyList<MetricObservation> Observations => _observations;

    /// <summary>One summary per fixed operation, ordered by its enum value.</summary>
    public IReadOnlyList<MetricOperationSummary> Summaries => _summaries;

    /// <summary>The exact number of retained observations.</summary>
    public int SampleCount => _observations.Count;

    /// <summary>Returns the summary for one defined operation.</summary>
    public MetricOperationSummary SummaryFor(MetricOperation operation)
    {
        MetricValidation.ValidateOperation(operation);
        return _summaries[(int)operation];
    }

    private static MetricOperationSummary[] BuildSummaries(MetricObservation[] observations)
    {
        var operations = Enum.GetValues<MetricOperation>();
        var summaries = new MetricOperationSummary[operations.Length];
        for (int index = 0; index < operations.Length; index++)
        {
            MetricOperation operation = operations[index];
            MetricObservation[] samples = observations
                .Where(observation => observation.Operation == operation)
                .ToArray();
            long[] completedDurations = samples
                .Where(observation => observation.Outcome == MetricOutcome.Completed)
                .Select(observation => observation.Duration.Ticks)
                .Order()
                .ToArray();

            summaries[index] = new MetricOperationSummary(
                operation,
                samples.Length,
                completedDurations.Length,
                samples.Count(observation => observation.Outcome == MetricOutcome.Cancelled),
                samples.Count(observation => observation.Outcome == MetricOutcome.Refused),
                samples.Count(observation => observation.Outcome == MetricOutcome.Failed),
                NearestRank(completedDurations, 50),
                NearestRank(completedDurations, 95));
        }

        return summaries;
    }

    private static TimeSpan? NearestRank(long[] sortedDurationTicks, int percentile)
    {
        if (sortedDurationTicks.Length == 0)
        {
            return null;
        }

        // Integer arithmetic is the exact ceil(percentile * N / 100), with a 1-based rank.
        int rank = checked((int)(((long)percentile * sortedDurationTicks.Length + 99) / 100));
        return TimeSpan.FromTicks(sortedDurationTicks[rank - 1]);
    }
}
