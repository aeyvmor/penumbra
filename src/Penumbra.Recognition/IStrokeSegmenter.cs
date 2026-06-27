using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Groups a flat stroke list into ordered symbol groups — Seam 1's first half (which strokes
/// form each symbol). Implementations return the groups left-to-right.
/// </summary>
public interface IStrokeSegmenter
{
    /// <summary>Group strokes into ordered (left-to-right) symbol groups.</summary>
    IReadOnlyList<StrokeGroup> Segment(IReadOnlyList<Stroke> strokes);
}
