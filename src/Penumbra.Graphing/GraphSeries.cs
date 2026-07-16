namespace Penumbra.Graphing;

/// <summary>One sampled point on a curve.</summary>
public readonly record struct GraphPoint(double X, double Y);

/// <summary>
/// A contiguous run of finite, gap-free points. <see cref="IDomainSampler"/> starts a new segment whenever a
/// sample is non-finite (an asymptote, a domain edge such as <c>sqrt(x)</c> for <c>x&lt;0</c>, …) so a
/// polyline renderer never draws a fake line across a gap.
/// </summary>
/// <remarks>
/// Implements structural (not reference) equality by comparing <see cref="Points"/> element-by-element — the
/// synthesized <c>record</c> equality would compare the backing list by reference instead, which would make
/// the "identical series for identical inputs" determinism contract untestable by simple equality.
/// </remarks>
public sealed class GraphSegment : IEquatable<GraphSegment>
{
    public GraphSegment(IReadOnlyList<GraphPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            throw new ArgumentException("a segment must have at least one point", nameof(points));
        }

        Points = points;
    }

    /// <summary>The ordered, gap-free points in this run.</summary>
    public IReadOnlyList<GraphPoint> Points { get; }

    /// <inheritdoc />
    public bool Equals(GraphSegment? other) => other is not null && Points.SequenceEqual(other.Points);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as GraphSegment);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (GraphPoint point in Points)
        {
            hash.Add(point);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// A graphable curve broken into gap-separated <see cref="GraphSegment"/>s. Never contains a fabricated point:
/// a non-finite or complex sample simply ends the current segment.
/// </summary>
/// <remarks>See <see cref="GraphSegment"/>'s remarks — equality here is likewise structural, not reference.</remarks>
public sealed class GraphSeries : IEquatable<GraphSeries>
{
    public GraphSeries(IReadOnlyList<GraphSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = segments;
    }

    /// <summary>The ordered, gap-separated segments. Empty when every sample was non-finite.</summary>
    public IReadOnlyList<GraphSegment> Segments { get; }

    /// <inheritdoc />
    public bool Equals(GraphSeries? other) => other is not null && Segments.SequenceEqual(other.Segments);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as GraphSeries);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (GraphSegment segment in Segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }
}
