using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Groups symbol boxes into horizontal lines by Y-projection — the 3.9f line-split, lifted out of
/// <see cref="ExpressionRecognizer"/> so both the single-line guard (which then recognizes the largest
/// line) and <see cref="RegionSegmenter"/> (which recognizes every line) share one implementation.
/// Line clustering is exactly y-interval overlap with a tolerance: two boxes join a line when the
/// vertical gap between them falls within ~0.7x the page's median symbol height. Keeping it in one
/// place means Phase 5's multi-line regions can never drift from the guard they generalize.
/// <para>
/// Phase 5.5 slice 5 adds a second "partition repair" pass (<see cref="MergeStructuralNeighbours"/>): a
/// stacked fraction or a tall radical can legitimately land in TWO or THREE separate Y-projection lines
/// (numerator / bar / denominator), but the spatial-grammar parser needs the whole structure as one
/// candidate. Classification hasn't run yet at this stage, so the merge test is purely geometric — a
/// bar-like (wide, flat) provisional line whose X-extent bridges its neighbour's content, or a group tall
/// enough to vertically envelop a whole neighbouring line — never a label. Independent page lines (no
/// bridging/enveloping shape between them) are untouched by construction, which is what keeps
/// unrelated multi-line pages (e.g. <c>a=2</c> / <c>b=1</c> / <c>y=ax+b</c>) at three separate regions.
/// </para>
/// </summary>
internal static class LineClustering
{
    /// <summary>A provisional line's group counts as "bar-like" (a fraction-bar candidate, not a symbol)
    /// once its width exceeds its height by at least this factor.</summary>
    private const double BarAspectRatio = 3.0;

    /// <summary>A bar-like group's height must stay under this fraction of the surrounding median symbol
    /// height — tall ink is not a flat bar, whatever its aspect ratio.</summary>
    private const double BarMaxHeightRatio = 0.5;

    /// <summary>Minimum fraction of the narrower width that must overlap in X for a bar (or an enveloping
    /// group) to count as bridging/covering a neighbour line's content.</summary>
    private const double BridgeOverlapRatio = 0.5;

    /// <summary>Largest vertical gap from a structural bar to an adjacent numerator/denominator line,
    /// relative to the page's median symbol height. This admits ordinary stacked handwriting but prevents a
    /// bar on one equation from reaching across normal page-line spacing.</summary>
    private const double StructuralNeighbourGapRatio = 1.5;

    /// <summary>An enveloping candidate's height must exceed the page's median symbol height by at least
    /// this factor to count as an unusually tall structural mark (a radical sign scaled to reach across
    /// what naive Y-projection treated as a line boundary) rather than just another normal-sized symbol.
    /// Kept comfortably above 1.0 so ordinary per-line height variance (e.g. one line of small digits next
    /// to one line of taller ink) never trips it.</summary>
    private const double TallMarkHeightRatio = 2.0;

    // Order boxes by vertical center, then cut a new line wherever the vertical gap down to the
    // current line's lower edge exceeds ~0.7x the page's median symbol height. The page median is a
    // stable scale (robust to one stray mark); 0.7x sits above intra-line baseline jitter yet below
    // normal line spacing (retuned down from 0.8 after s19 dogfood, where two expressions written
    // nearly touching — gap ≈ 0.75x — fused into one garbled region). Lines come back top-to-bottom;
    // groups within a line keep the center-sorted order (callers reorder left-to-right for assembly).
    public static List<List<StrokeGroup>> IntoLines(IReadOnlyList<StrokeGroup> groups)
    {
        if (groups.Count == 0)
        {
            return new List<List<StrokeGroup>>();
        }

        List<List<StrokeGroup>> provisional = ClusterByYProjection(groups);
        return MergeStructuralNeighbours(provisional);
    }

    private static List<List<StrokeGroup>> ClusterByYProjection(IReadOnlyList<StrokeGroup> groups)
    {
        double[] heights = groups.Select(g => g.Bounds.Height).OrderBy(h => h).ToArray();
        double medianHeight = heights[heights.Length / 2];
        double threshold = 0.7 * (medianHeight > 0 ? medianHeight : 1.0);

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

    /// <summary>
    /// Resolves structural relationships over the Y-projection's provisional lines. A fraction bar must
    /// bridge content on BOTH vertical sides; one wide subtraction stroke plus one unrelated line below is
    /// never enough. A tall radical relationship requires actual vertical envelopment, not height alone.
    /// The relationships union only adjacent provisional lines, after which the original top-to-bottom and
    /// per-line group ordering is preserved.
    /// </summary>
    private static List<List<StrokeGroup>> MergeStructuralNeighbours(List<List<StrokeGroup>> lines)
    {
        if (lines.Count < 2)
        {
            return lines;
        }

        double medianHeight = MedianHeight(lines.SelectMany(line => line));

        var parent = Enumerable.Range(0, lines.Count).ToArray();

        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }

            return value;
        }

        void Union(int a, int b) => parent[Find(b)] = Find(a);

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            List<StrokeGroup> line = lines[lineIndex];
            foreach (StrokeGroup bar in line.Where(group => IsBarLike(group, medianHeight)))
            {
                bool hasBaselinePeer = line.Any(group => !ReferenceEquals(group, bar)
                    && IsBaselinePeer(bar.Bounds, group.Bounds, medianHeight));
                if (hasBaselinePeer)
                {
                    // This is a subtraction/operator inside a normal expression line. Even if close page
                    // lines happen to sit above and below it, it is not an isolated structural bar.
                    continue;
                }

                bool ownAbove = line.Any(group => !ReferenceEquals(group, bar)
                    && IsClearSide(bar.Bounds, group.Bounds, above: true, medianHeight));
                bool ownBelow = line.Any(group => !ReferenceEquals(group, bar)
                    && IsClearSide(bar.Bounds, group.Bounds, above: false, medianHeight));

                bool externalAbove = lineIndex > 0
                    && LineProvidesSide(bar.Bounds, lines[lineIndex - 1], above: true, medianHeight);
                bool externalBelow = lineIndex + 1 < lines.Count
                    && LineProvidesSide(bar.Bounds, lines[lineIndex + 1], above: false, medianHeight);

                if ((ownAbove || externalAbove) && (ownBelow || externalBelow))
                {
                    if (externalAbove)
                    {
                        Union(lineIndex - 1, lineIndex);
                    }

                    if (externalBelow)
                    {
                        Union(lineIndex, lineIndex + 1);
                    }
                }
            }
        }

        for (int i = 0; i < lines.Count - 1; i++)
        {
            if (HasEnvelopingGroup(lines[i], lines[i + 1], medianHeight)
                || HasEnvelopingGroup(lines[i + 1], lines[i], medianHeight))
            {
                Union(i, i + 1);
            }
        }

        var merged = new List<List<StrokeGroup>>();
        var byRoot = new Dictionary<int, List<StrokeGroup>>();
        for (int i = 0; i < lines.Count; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out List<StrokeGroup>? collected))
            {
                collected = new List<StrokeGroup>();
                byRoot[root] = collected;
                merged.Add(collected);
            }

            collected.AddRange(lines[i]);
        }

        return merged;
    }

    private static bool IsBarLike(StrokeGroup group, double medianHeight) =>
        group.Bounds.Height > 0
        && group.Bounds.Width > group.Bounds.Height * BarAspectRatio
        && group.Bounds.Height <= medianHeight * BarMaxHeightRatio;

    private static bool LineProvidesSide(
        InkBounds bar, IReadOnlyList<StrokeGroup> line, bool above, double medianHeight)
    {
        InkBounds bounds = UnionBounds(line);
        double gap = above
            ? bar.Y - (bounds.Y + bounds.Height)
            : bounds.Y - (bar.Y + bar.Height);
        return gap >= 0
            && gap <= StructuralNeighbourGapRatio * medianHeight
            && XOverlapRatio(bar, bounds) >= BridgeOverlapRatio
            && line.Any(group => IsClearSide(bar, group.Bounds, above, medianHeight));
    }

    private static bool IsClearSide(InkBounds bar, InkBounds other, bool above, double medianHeight)
    {
        if (XOverlapRatio(bar, other) < BridgeOverlapRatio)
        {
            return false;
        }

        double tolerance = 0.05 * medianHeight;
        return above
            ? other.Y + other.Height <= bar.Y + tolerance
            : other.Y >= bar.Y + bar.Height - tolerance;
    }

    private static bool IsBaselinePeer(InkBounds bar, InkBounds other, double medianHeight)
    {
        double barMid = bar.Y + bar.Height / 2.0;
        double otherMid = other.Y + other.Height / 2.0;
        return Math.Abs(otherMid - barMid) < 0.2 * medianHeight;
    }

    /// <summary>
    /// True when some unusually tall group actually encloses the neighbour line vertically (within a small
    /// handwriting tolerance) and overlaps it in X. Height alone is insufficient: that old rule could join
    /// two ordinary equations merely because one happened to contain a tall glyph.
    /// </summary>
    private static bool HasEnvelopingGroup(
        List<StrokeGroup> maybeEnveloper, List<StrokeGroup> maybeEnveloped, double medianHeight)
    {
        InkBounds envelopedBounds = UnionBounds(maybeEnveloped);
        foreach (StrokeGroup g in maybeEnveloper)
        {
            bool unusuallyTall = g.Bounds.Height > medianHeight * TallMarkHeightRatio;
            bool overlapsX = XOverlapRatio(g.Bounds, envelopedBounds) >= BridgeOverlapRatio;
            double tolerance = 0.15 * medianHeight;
            bool envelopsY = g.Bounds.Y <= envelopedBounds.Y + tolerance
                && g.Bounds.Y + g.Bounds.Height >=
                    envelopedBounds.Y + envelopedBounds.Height - tolerance;
            if (unusuallyTall && overlapsX && envelopsY)
            {
                return true;
            }
        }

        return false;
    }

    private static double XOverlapRatio(InkBounds bar, InkBounds other)
    {
        double overlap = Math.Min(bar.X + bar.Width, other.X + other.Width) - Math.Max(bar.X, other.X);
        double narrower = Math.Min(bar.Width, other.Width);
        return narrower <= 0 ? 0 : Math.Max(0, overlap) / narrower;
    }

    private static InkBounds UnionBounds(IEnumerable<StrokeGroup> groups)
    {
        List<StrokeGroup> list = groups.ToList();
        double xMin = list.Min(g => g.Bounds.X);
        double yMin = list.Min(g => g.Bounds.Y);
        double xMax = list.Max(g => g.Bounds.X + g.Bounds.Width);
        double yMax = list.Max(g => g.Bounds.Y + g.Bounds.Height);
        return new InkBounds(xMin, yMin, xMax - xMin, yMax - yMin);
    }

    private static double MedianHeight(IEnumerable<StrokeGroup> groups)
    {
        double[] heights = groups.Select(g => g.Bounds.Height).OrderBy(h => h).ToArray();
        if (heights.Length == 0)
        {
            return 1.0;
        }

        int mid = heights.Length / 2;
        double median = heights.Length % 2 == 1
            ? heights[mid]
            : (heights[mid - 1] + heights[mid]) / 2.0;
        return median > 0 ? median : 1.0;
    }
}
