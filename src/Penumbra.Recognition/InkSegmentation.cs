namespace Penumbra.Recognition;

/// <summary>
/// The line-<see cref="InkRegion"/>s of one page, top-to-bottom in reading order. Passed back into
/// <see cref="IRegionSegmenter.Segment(System.Collections.Generic.IReadOnlyList{Penumbra.Core.Stroke}, InkSegmentation?)"/>
/// on the next edit so region ids carry forward.
/// </summary>
public sealed record InkSegmentation(IReadOnlyList<InkRegion> Regions);
