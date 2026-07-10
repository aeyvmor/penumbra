namespace Penumbra.Recognition;

/// <summary>
/// Groups symbol boxes into horizontal lines by Y-projection — the 3.9f line-split, lifted out of
/// <see cref="ExpressionRecognizer"/> so both the single-line guard (which then recognizes the largest
/// line) and <see cref="RegionSegmenter"/> (which recognizes every line) share one implementation.
/// Line clustering is exactly y-interval overlap with a tolerance: two boxes join a line when the
/// vertical gap between them falls within ~0.8x the page's median symbol height. Keeping it in one
/// place means Phase 5's multi-line regions can never drift from the guard they generalize.
/// </summary>
internal static class LineClustering
{
    // Order boxes by vertical center, then cut a new line wherever the vertical gap down to the
    // current line's lower edge exceeds ~0.8x the page's median symbol height. The page median is a
    // stable scale (robust to one stray mark); 0.8x sits comfortably above intra-line baseline jitter
    // yet below normal line spacing. Lines come back top-to-bottom; groups within a line keep the
    // center-sorted order (callers reorder left-to-right for reading/assembly).
    public static List<List<StrokeGroup>> IntoLines(IReadOnlyList<StrokeGroup> groups)
    {
        if (groups.Count == 0)
        {
            return new List<List<StrokeGroup>>();
        }

        double[] heights = groups.Select(g => g.Bounds.Height).OrderBy(h => h).ToArray();
        double medianHeight = heights[heights.Length / 2];
        double threshold = 0.8 * (medianHeight > 0 ? medianHeight : 1.0);

        var lines = new List<List<StrokeGroup>>();
        var current = new List<StrokeGroup>();
        double lineYMax = double.NegativeInfinity;

        foreach (StrokeGroup group in groups.OrderBy(g => g.Bounds.Y + g.Bounds.Height / 2.0))
        {
            if (current.Count > 0 && group.Bounds.Y - lineYMax > threshold)
            {
                lines.Add(current);
                current = new List<StrokeGroup>();
                lineYMax = double.NegativeInfinity;
            }

            current.Add(group);
            lineYMax = Math.Max(lineYMax, group.Bounds.Y + group.Bounds.Height);
        }

        lines.Add(current);
        return lines;
    }
}
