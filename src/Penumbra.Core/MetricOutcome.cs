namespace Penumbra.Core;

/// <summary>Terminal outcome of one locally observed operation.</summary>
public enum MetricOutcome
{
    Completed = 0,
    Cancelled = 1,
    Refused = 2,
    Failed = 3,
}
