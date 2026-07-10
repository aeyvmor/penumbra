using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Clusters a page of strokes into ordered line-<see cref="InkRegion"/>s (Phase 5a). Generalizes the
/// 3.9f y-projection line-split from "keep the largest line" to "keep every line as its own region",
/// and — given the previous segmentation — keeps region ids stable across edits so downstream state
/// (recognition results, dependency-graph nodes) can be reused for regions the edit didn't touch.
/// </summary>
public interface IRegionSegmenter
{
    /// <summary>Segment a page into line-regions with fresh ids (no prior segmentation to match against).</summary>
    InkSegmentation Segment(IReadOnlyList<Stroke> strokes);

    /// <summary>
    /// Segment a page and carry ids forward from <paramref name="previous"/> by stroke-set overlap, so a
    /// region the edit left alone keeps its id. Pass <c>null</c> for a fresh segmentation.
    /// </summary>
    InkSegmentation Segment(IReadOnlyList<Stroke> strokes, InkSegmentation? previous);
}
