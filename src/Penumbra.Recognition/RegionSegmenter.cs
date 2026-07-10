using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Phase 5a region segmenter: runs the per-symbol <see cref="IStrokeSegmenter"/>, then clusters the
/// resulting groups into horizontal line-regions via <see cref="LineClustering"/> (the 3.9f y-projection
/// split, generalized from "keep one line" to "every line is a region"). When a previous segmentation is
/// supplied, region ids are carried forward by stroke-set overlap so an edit to one line leaves the other
/// lines' ids — and therefore their cached recognition — untouched.
///
/// The id-matching idea (a set of stroke ids as a region's identity) is borrowed from
/// <see cref="GlyphCapture"/>'s stroke-set dedup, but deliberately not coupled to it: dedup asks "did I
/// bank this exact ink already?" (exact-set equality), whereas matching asks "which prior region is this
/// mostly the same as?" (maximum overlap), because an edit changes a region's set without replacing it.
/// </summary>
public sealed class RegionSegmenter : IRegionSegmenter
{
    private readonly IStrokeSegmenter _segmenter;

    public RegionSegmenter(IStrokeSegmenter segmenter)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        _segmenter = segmenter;
    }

    /// <inheritdoc />
    public InkSegmentation Segment(IReadOnlyList<Stroke> strokes) => Segment(strokes, previous: null);

    /// <inheritdoc />
    public InkSegmentation Segment(IReadOnlyList<Stroke> strokes, InkSegmentation? previous)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(strokes);
        if (groups.Count == 0)
        {
            return new InkSegmentation(Array.Empty<InkRegion>());
        }

        // The page-wide pass only decides which strokes share a line. Symbol grouping inside each line
        // is then REDONE over that line's strokes alone, because the segmenter scales its merge gaps by
        // the median stroke size of whatever it is given: fed the whole page, small ink on one line
        // shrinks the gaps for every other line, so a fresh read of an untouched '=' could split into
        // two minus signs the moment unrelated ink appeared elsewhere (s19 dogfood). Line-local
        // regrouping makes a line's read a function of that line's ink only. For a single-line page the
        // rerun sees the identical stroke set, so this path still matches the pre-5a page path exactly.
        Dictionary<Stroke, int> documentOrder = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < strokes.Count; i++)
        {
            documentOrder.TryAdd(strokes[i], i);
        }

        // Each clustered line becomes a region; groups within it are ordered left-to-right (X, then Y),
        // matching the reading order ExpressionRecognizer assembles in.
        List<(IReadOnlyList<StrokeGroup> Groups, IReadOnlyList<Guid> StrokeIds, InkBounds Bounds)> lines =
            LineClustering.IntoLines(groups)
                .Select(line =>
                {
                    List<Stroke> lineStrokes = line
                        .SelectMany(g => g.Strokes)
                        .OrderBy(s => documentOrder[s])   // line-local pass sees document draw order
                        .ToList();
                    List<StrokeGroup> ordered = _segmenter.Segment(lineStrokes)
                        .OrderBy(g => g.Bounds.X)
                        .ThenBy(g => g.Bounds.Y)
                        .ToList();
                    IReadOnlyList<Guid> ids = ordered
                        .SelectMany(g => g.Strokes.Select(s => s.Id))
                        .OrderBy(id => id)
                        .ToList();
                    return ((IReadOnlyList<StrokeGroup>)ordered, ids, LineBounds(ordered));
                })
                .ToList();

        Guid[] ids = AssignIds(lines.Select(l => l.StrokeIds).ToList(), previous);

        var regions = new List<InkRegion>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            regions.Add(new InkRegion(ids[i], lines[i].StrokeIds, lines[i].Bounds, lines[i].Groups));
        }

        return new InkSegmentation(regions);
    }

    // Give each new region a stable id: reuse the id of the previous region it overlaps most (by shared
    // stroke count), and mint a fresh id when nothing overlaps. Greedy over all positive-overlap pairs,
    // strongest first, one previous id claimed at most once — so a line the edit didn't touch (100%
    // overlap) always out-bids partial matches for its own id, and an edited line still recovers its id
    // as long as it kept the plurality of its strokes.
    private static Guid[] AssignIds(
        IReadOnlyList<IReadOnlyList<Guid>> newRegions, InkSegmentation? previous)
    {
        var assigned = new Guid?[newRegions.Count];
        IReadOnlyList<InkRegion> prev = previous?.Regions ?? Array.Empty<InkRegion>();

        if (prev.Count > 0)
        {
            var newSets = newRegions.Select(r => new HashSet<Guid>(r)).ToList();

            var candidates = new List<(int NewIdx, int PrevIdx, int Overlap)>();
            for (int n = 0; n < newSets.Count; n++)
            {
                for (int p = 0; p < prev.Count; p++)
                {
                    int overlap = prev[p].StrokeIds.Count(newSets[n].Contains);
                    if (overlap > 0)
                    {
                        candidates.Add((n, p, overlap));
                    }
                }
            }

            var prevClaimed = new bool[prev.Count];
            foreach ((int newIdx, int prevIdx, int _) in candidates
                         .OrderByDescending(c => c.Overlap)
                         .ThenBy(c => c.NewIdx)
                         .ThenBy(c => c.PrevIdx))
            {
                if (assigned[newIdx] is null && !prevClaimed[prevIdx])
                {
                    assigned[newIdx] = prev[prevIdx].Id;
                    prevClaimed[prevIdx] = true;
                }
            }
        }

        var result = new Guid[newRegions.Count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = assigned[i] ?? Guid.NewGuid();
        }

        return result;
    }

    private static InkBounds LineBounds(IReadOnlyList<StrokeGroup> groups)
    {
        double xMin = groups.Min(g => g.Bounds.X);
        double yMin = groups.Min(g => g.Bounds.Y);
        double xMax = groups.Max(g => g.Bounds.X + g.Bounds.Width);
        double yMax = groups.Max(g => g.Bounds.Y + g.Bounds.Height);
        return new InkBounds(xMin, yMin, xMax - xMin, yMax - yMin);
    }
}
