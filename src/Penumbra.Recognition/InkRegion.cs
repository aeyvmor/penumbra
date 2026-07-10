using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// A line-region of the page: the ordered symbol <see cref="Groups"/> the segmenter clustered onto one
/// horizontal line, plus a <see cref="Id"/> that stays stable across edits (matched by stroke-set overlap
/// in <see cref="RegionSegmenter"/>) so a region's recognition and dependency-graph node survive
/// re-segmentation. This is the Phase-5 unit of incremental recognition — the old page-wide "one x-sorted
/// line" (audit A7) becomes one region among many.
/// </summary>
public sealed record InkRegion(
    Guid Id,
    IReadOnlyList<Guid> StrokeIds,
    InkBounds Bounds,
    IReadOnlyList<StrokeGroup> Groups)
{
    /// <summary>
    /// True when this region covers the exact same physical strokes as <paramref name="other"/>, order
    /// aside — the "unchanged since last recognition" test that drives dirty tracking. Compared as sets
    /// because segmentation order is not part of a region's identity.
    /// </summary>
    public bool HasSameStrokes(InkRegion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (StrokeIds.Count != other.StrokeIds.Count)
        {
            return false;
        }

        var mine = new HashSet<Guid>(StrokeIds);
        return other.StrokeIds.All(mine.Contains);
    }
}
