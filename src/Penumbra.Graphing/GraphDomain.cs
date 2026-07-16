namespace Penumbra.Graphing;

/// <summary>
/// A validated sampling domain <c>[Min, Max]</c>. Construction is the enforcement point: a caller can never
/// hold a <see cref="GraphDomain"/> with a non-finite bound or an empty/inverted range.
/// </summary>
public sealed record GraphDomain
{
    private GraphDomain(double min, double max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>The inclusive lower bound.</summary>
    public double Min { get; }

    /// <summary>The inclusive upper bound.</summary>
    public double Max { get; }

    /// <summary>
    /// Builds a domain, rejecting a non-finite bound or a range that is not strictly increasing.
    /// </summary>
    public static GraphDomain Create(double min, double max)
    {
        if (!double.IsFinite(min))
        {
            throw new ArgumentOutOfRangeException(nameof(min), min, "domain minimum must be finite");
        }

        if (!double.IsFinite(max))
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "domain maximum must be finite");
        }

        if (!(min < max))
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "domain maximum must be greater than the minimum");
        }

        return new GraphDomain(min, max);
    }
}
