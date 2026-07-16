namespace Penumbra.Graphing;

/// <summary>Top-level verdict of <see cref="IDomainSampler"/>.</summary>
public enum GraphSamplingOutcomeKind
{
    /// <summary>A series was produced; <see cref="GraphSamplingOutcome.Series"/> is present.</summary>
    Sampled,

    /// <summary>Sampling was refused; no series is offered.</summary>
    Refused,
}

/// <summary>Machine-readable reason a domain sample was refused.</summary>
public enum GraphSamplingRefusalReason
{
    /// <summary>Not refused (a sampled outcome).</summary>
    None,

    /// <summary>
    /// The candidate's right-hand side could not be compiled into a numeric lambda. The detector's own
    /// contract (a single free variable, no unsupported construct) makes this rare in practice; this exists
    /// so an unexpected AngouriMath compilation failure refuses honestly instead of throwing to the caller.
    /// </summary>
    UncompilableExpression,
}

/// <summary>
/// Typed sampled/refused contract carried by domain sampling. A <see cref="Series"/> exists only on
/// <see cref="GraphSamplingOutcomeKind.Sampled"/>; a refusal carries a concrete
/// <see cref="GraphSamplingRefusalReason"/> and optional <see cref="Detail"/> and never a series.
/// </summary>
public sealed record GraphSamplingOutcome
{
    private GraphSamplingOutcome(
        GraphSamplingOutcomeKind kind, GraphSeries? series, GraphSamplingRefusalReason reason, string? detail)
    {
        Kind = kind;
        Series = series;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The verdict.</summary>
    public GraphSamplingOutcomeKind Kind { get; }

    /// <summary>The sampled series, or null for a refusal.</summary>
    public GraphSeries? Series { get; }

    /// <summary>The refusal category; <see cref="GraphSamplingRefusalReason.None"/> when sampled.</summary>
    public GraphSamplingRefusalReason Reason { get; }

    /// <summary>Optional human-readable context.</summary>
    public string? Detail { get; }

    /// <summary>Convenience: true only for a sampled outcome (implies a non-null <see cref="Series"/>).</summary>
    public bool IsSampled => Kind == GraphSamplingOutcomeKind.Sampled;

    /// <summary>Builds a sampled outcome around a non-null series.</summary>
    public static GraphSamplingOutcome Sampled(GraphSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);
        return new GraphSamplingOutcome(GraphSamplingOutcomeKind.Sampled, series, GraphSamplingRefusalReason.None, detail: null);
    }

    /// <summary>Builds a refusal with a concrete, non-<see cref="GraphSamplingRefusalReason.None"/> reason.</summary>
    public static GraphSamplingOutcome Refused(GraphSamplingRefusalReason reason, string? detail = null)
    {
        if (reason == GraphSamplingRefusalReason.None)
        {
            throw new ArgumentException("a refusal needs a concrete reason", nameof(reason));
        }

        return new GraphSamplingOutcome(GraphSamplingOutcomeKind.Refused, series: null, reason, detail);
    }
}
