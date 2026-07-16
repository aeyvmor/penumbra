namespace Penumbra.Graphing;

/// <summary>Top-level verdict of <see cref="IGraphDetector"/>.</summary>
public enum GraphDetectionOutcomeKind
{
    /// <summary>A graphable candidate was produced; <see cref="GraphDetectionOutcome.Candidate"/> is present.</summary>
    Accepted,

    /// <summary>The expression is not graphable as an explicit function; no candidate is offered.</summary>
    Rejected,
}

/// <summary>
/// Typed accepted/rejected contract carried by graph detection. A <see cref="Candidate"/> exists only on
/// <see cref="GraphDetectionOutcomeKind.Accepted"/>; a rejection carries a concrete
/// <see cref="GraphRejectionReason"/> and optional <see cref="Detail"/> and never a candidate.
/// </summary>
public sealed record GraphDetectionOutcome
{
    private GraphDetectionOutcome(
        GraphDetectionOutcomeKind kind, GraphCandidate? candidate, GraphRejectionReason reason, string? detail)
    {
        Kind = kind;
        Candidate = candidate;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The verdict.</summary>
    public GraphDetectionOutcomeKind Kind { get; }

    /// <summary>The accepted candidate, or null for a rejection.</summary>
    public GraphCandidate? Candidate { get; }

    /// <summary>The rejection category; <see cref="GraphRejectionReason.None"/> when accepted.</summary>
    public GraphRejectionReason Reason { get; }

    /// <summary>Optional human-readable context; never fed back into the CAS.</summary>
    public string? Detail { get; }

    /// <summary>Convenience: true only for an accepted outcome (implies a non-null <see cref="Candidate"/>).</summary>
    public bool IsAccepted => Kind == GraphDetectionOutcomeKind.Accepted;

    /// <summary>Builds an accepted outcome around a non-null candidate.</summary>
    public static GraphDetectionOutcome Accepted(GraphCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return new GraphDetectionOutcome(GraphDetectionOutcomeKind.Accepted, candidate, GraphRejectionReason.None, detail: null);
    }

    /// <summary>Builds a rejection with a concrete, non-<see cref="GraphRejectionReason.None"/> reason.</summary>
    public static GraphDetectionOutcome Rejected(GraphRejectionReason reason, string? detail = null)
    {
        if (reason == GraphRejectionReason.None)
        {
            throw new ArgumentException("a rejection needs a concrete reason", nameof(reason));
        }

        return new GraphDetectionOutcome(GraphDetectionOutcomeKind.Rejected, candidate: null, reason, detail);
    }
}
