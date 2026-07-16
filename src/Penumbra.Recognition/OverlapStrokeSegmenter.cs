using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// M1 segmenter: spatial-overlap clustering. Two strokes join the same symbol when their bounding
/// boxes overlap or sit within a small gap — horizontally tight (adjacent symbols are separated by
/// the space between them) but vertically generous (a symbol's own strokes can stack: the bars of
/// <c>=</c>, a dot over its stem, the two strokes of <c>÷</c>). Groups come back ordered
/// left-to-right by their left edge.
///
/// Gaps are measured relative to a reference symbol size (the median stroke extent) so the
/// thresholds are scale-independent. Known hard cases left for later (logged, not solved here):
/// two genuinely separate symbols whose ink touches/overlaps, <c>=</c> vs two minus signs, and a
/// dot drawn far from its stem. See docs/phase-3-step5-segmentation.md (5a).
/// </summary>
public sealed class OverlapStrokeSegmenter : IStrokeSegmenter
{
    private const double StructuralBarAspectRatio = 3.0;
    private const double StructuralBarMinimumWidthRatio = 0.75;
    private const double StructuralSideOverlapRatio = 0.5;
    private const double StructuralSideMaximumGapRatio = 1.5;
    private const double StructuralSideMinimumReferenceExtentRatio = 0.35;
    private const double StructuralSideMinimumBarExtentRatio = 0.25;
    private const double ParallelBarWidthRatio = 0.75;
    private const double ParallelBarMaximumGapRatio = 0.75;
    private const double StackedStrokeMinimumGapRatio = 0.45;
    private const double StackedStrokeMinimumHeightRatio = 0.5;
    private const double StackedStrokeMinimumXOverlapRatio = 0.8;
    private const double StackedStrokeMinimumHeightSimilarityRatio = 0.5;

    private readonly double _horizontalGapFactor;
    private readonly double _verticalGapFactor;

    /// <param name="horizontalGapFactor">Largest horizontal gap that still merges, as a fraction of
    /// the reference symbol size. Kept small so adjacent symbols stay apart.</param>
    /// <param name="verticalGapFactor">Largest vertical gap that still merges, as a fraction of the
    /// reference symbol size. Kept larger so a symbol's stacked strokes stay together.</param>
    // 3.9g: retuned down from 0.4 / 1.2 after dogfooding on large mouse ink (~150-190px symbols),
    // where the old radii (a 60-75px horizontal swallow, wider than real inter-symbol spacing)
    // chain-merged SEPARATE symbols into one blob that the CNN then confidently mislabelled
    // ('5-1=' read as '5=', '27' read as '2'). Genuine same-symbol strokes barely need a horizontal
    // window: the cross of a '+', the arms of an '=' / '÷', the bar+stem of 4/5/7 all overlap or
    // touch in x (dx≈0), so gapX only has to absorb sloppy near-misses, not bridge the space between
    // symbols. Vertically, a symbol's own stacked strokes (the two bars of '=', a '÷' dot over its
    // bar) sit within ~0.5x the symbol size, so 0.6 covers them while staying below line spacing —
    // and, crucially, below the vertical distance from an operator to a digit on the same line, so a
    // '+' whose bar happens to overlap a neighbouring '7' in x no longer swallows it.
    public OverlapStrokeSegmenter(double horizontalGapFactor = 0.1, double verticalGapFactor = 0.6)
    {
        _horizontalGapFactor = horizontalGapFactor;
        _verticalGapFactor = verticalGapFactor;
    }

    /// <inheritdoc />
    public IReadOnlyList<StrokeGroup> Segment(IReadOnlyList<Stroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        // Only strokes that actually drew something carry spatial information.
        var items = strokes
            .Where(s => s.Samples.Count > 0)
            .Select(s => new StrokeItem(s, SymbolPreprocessor.Bounds(new[] { s })))
            .ToList();
        if (items.Count == 0)
        {
            return Array.Empty<StrokeGroup>();
        }

        double reference = ReferenceSize(items.Select(i => i.Box));
        double gapX = _horizontalGapFactor * reference;
        double gapY = _verticalGapFactor * reference;
        IReadOnlySet<int> structuralBars = FindStructuralBars(items, reference);

        // Union-find: merge strokes whose boxes are within the (gapX, gapY) window of each other.
        var parent = Enumerable.Range(0, items.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                if (Close(items[i].Box, items[j].Box, gapX, gapY)
                    && !MustRemainSeparate(items, i, j, structuralBars, reference))
                {
                    Union(i, j);
                }
            }
        }

        // Collect strokes per root, preserving the original stroke order within each group.
        var byRoot = new Dictionary<int, List<Stroke>>();
        for (int i = 0; i < items.Count; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out List<Stroke>? list))
            {
                byRoot[root] = list = new List<Stroke>();
            }
            list.Add(items[i].Stroke);
        }

        // Order left-to-right by left edge; ThenBy(Y) is a stable, deterministic tie-break for the
        // rare case of two groups sharing an x (OrderBy is a stable sort, Dictionary preserves order).
        return byRoot.Values
            .Select(g => new StrokeGroup(g, SymbolPreprocessor.Bounds(g)))
            .OrderBy(g => g.Bounds.X)
            .ThenBy(g => g.Bounds.Y)
            .ToList();
    }

    // Median of each stroke's LARGER dimension — a robust, scale-only proxy for "how big is one
    // symbol here", used purely to scale the merge gaps. Deliberately max(w,h), NOT height alone:
    // flat strokes (a minus/fraction bar, the two bars of '=') have ~0 height, and keying off height
    // would zero out their size, collapse the gap thresholds, and wrongly split such symbols. This is
    // a different quantity from the feature ref_h in ExpressionRecognizer.LineContext (which mirrors
    // crohme.py and is correctly height-only) — segmentation itself is not part of that contract.
    private static double ReferenceSize(IEnumerable<InkBounds> boxes)
    {
        var sizes = boxes
            .Select(b => Math.Max(b.Width, b.Height))
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();
        if (sizes.Count == 0)
        {
            return 1.0;
        }

        int mid = sizes.Count / 2;
        double median = sizes.Count % 2 == 1 ? sizes[mid] : (sizes[mid - 1] + sizes[mid]) / 2.0;
        return median > 0 ? median : 1.0;
    }

    // Axis-aligned gap between two boxes (0 on an axis where they overlap); merge if within both gaps.
    private static bool Close(InkBounds a, InkBounds b, double gapX, double gapY)
    {
        double dx = Math.Max(0, Math.Max(a.X, b.X) - Math.Min(a.X + a.Width, b.X + b.Width));
        double dy = Math.Max(0, Math.Max(a.Y, b.Y) - Math.Min(a.Y + a.Height, b.Y + b.Height));
        return dx <= gapX && dy <= gapY;
    }

    // A close-written stacked fraction is the one case where the ordinary same-symbol proximity rule loses
    // structural information: the bridge bar can touch both neighbouring glyphs and chain-union the whole
    // fraction into one high-confidence division symbol. Preserve a bar as its own hypothesis only when it
    // has substantial, clearly separated ink on BOTH sides. Small division dots do not meet the substantial
    // side test; a plus has a stroke crossing the bar; '=' has a similar parallel peer; and a two-stroke 5/7
    // has ink on only one side. Those counter-shapes must retain the original merge semantics.
    private static IReadOnlySet<int> FindStructuralBars(IReadOnlyList<StrokeItem> items, double reference)
    {
        var result = new HashSet<int>();
        for (int index = 0; index < items.Count; index++)
        {
            InkBounds bar = items[index].Box;
            if (!LooksLikeBar(bar, reference))
            {
                continue;
            }

            var above = new List<InkBounds>();
            var below = new List<InkBounds>();
            bool crossingStroke = false;
            bool parallelPeer = false;
            double barMid = bar.Y + bar.Height / 2.0;
            double edgeTolerance = 0.05 * reference;

            for (int otherIndex = 0; otherIndex < items.Count; otherIndex++)
            {
                if (otherIndex == index)
                {
                    continue;
                }

                InkBounds other = items[otherIndex].Box;
                if (XOverlapRatio(bar, other) < StructuralSideOverlapRatio)
                {
                    continue;
                }

                if (LooksLikeParallelPeer(bar, other, reference))
                {
                    parallelPeer = true;
                    break;
                }

                if (other.Y < barMid && other.Y + other.Height > barMid)
                {
                    crossingStroke = true;
                    break;
                }

                double aboveGap = bar.Y - (other.Y + other.Height);
                if (other.Y + other.Height <= bar.Y + edgeTolerance
                    && aboveGap <= StructuralSideMaximumGapRatio * reference)
                {
                    above.Add(other);
                    continue;
                }

                double belowGap = other.Y - (bar.Y + bar.Height);
                if (other.Y >= bar.Y + bar.Height - edgeTolerance
                    && belowGap <= StructuralSideMaximumGapRatio * reference)
                {
                    below.Add(other);
                }
            }

            if (!crossingStroke
                && !parallelPeer
                && HasSubstantialSide(above, bar, reference)
                && HasSubstantialSide(below, bar, reference))
            {
                result.Add(index);
            }
        }

        return result;
    }

    private static bool MustRemainSeparate(
        IReadOnlyList<StrokeItem> items,
        int first,
        int second,
        IReadOnlySet<int> structuralBars,
        double reference)
    {
        InkBounds a = items[first].Box;
        InkBounds b = items[second].Box;
        if (ClearlySeparateStackedStrokes(a, b, reference))
        {
            return true;
        }

        if (structuralBars.Contains(first) || structuralBars.Contains(second))
        {
            return true;
        }

        double tolerance = 0.05 * reference;
        foreach (int barIndex in structuralBars)
        {
            InkBounds bar = items[barIndex].Box;
            bool aAbove = a.Y + a.Height <= bar.Y + tolerance;
            bool aBelow = a.Y >= bar.Y + bar.Height - tolerance;
            bool bAbove = b.Y + b.Height <= bar.Y + tolerance;
            bool bBelow = b.Y >= bar.Y + bar.Height - tolerance;
            if (((aAbove && bBelow) || (aBelow && bAbove))
                && XOverlapRatio(bar, a) >= StructuralSideOverlapRatio
                && XOverlapRatio(bar, b) >= StructuralSideOverlapRatio)
            {
                return true;
            }
        }

        return false;
    }

    // The page-wide reference can be a little larger than two compact samples written one below the
    // other, causing the closest diagonal strokes to chain-union both glyphs. Two substantial strokes with
    // a real vertical gap and near-total X overlap are separate rows. Same-glyph stacked pieces (=, ÷,
    // dotted letters, and top bars) are flat/small and deliberately fail the substantial-height guard.
    private static bool ClearlySeparateStackedStrokes(InkBounds a, InkBounds b, double reference)
    {
        double gap = Math.Max(0, Math.Max(a.Y, b.Y)
            - Math.Min(a.Y + a.Height, b.Y + b.Height));
        double shorterHeight = Math.Min(a.Height, b.Height);
        double tallerHeight = Math.Max(a.Height, b.Height);
        return gap >= StackedStrokeMinimumGapRatio * reference
            && a.Height >= StackedStrokeMinimumHeightRatio * reference
            && b.Height >= StackedStrokeMinimumHeightRatio * reference
            && tallerHeight > 0
            && shorterHeight / tallerHeight >= StackedStrokeMinimumHeightSimilarityRatio
            && XOverlapRatio(a, b) >= StackedStrokeMinimumXOverlapRatio;
    }

    private static bool LooksLikeBar(InkBounds bounds, double reference) =>
        bounds.Width >= Math.Max(1.0, bounds.Height) * StructuralBarAspectRatio
        && bounds.Width >= StructuralBarMinimumWidthRatio * reference;

    private static bool LooksLikeParallelPeer(InkBounds bar, InkBounds other, double reference)
    {
        if (!LooksLikeBar(other, reference))
        {
            return false;
        }

        double narrower = Math.Min(bar.Width, other.Width);
        double wider = Math.Max(bar.Width, other.Width);
        if (wider <= 0 || narrower / wider < ParallelBarWidthRatio)
        {
            return false;
        }

        double gap = Math.Max(0, Math.Max(bar.Y, other.Y)
            - Math.Min(bar.Y + bar.Height, other.Y + other.Height));
        return gap <= ParallelBarMaximumGapRatio * reference;
    }

    private static bool HasSubstantialSide(
        IReadOnlyList<InkBounds> side,
        InkBounds bar,
        double reference)
    {
        if (side.Count == 0)
        {
            return false;
        }

        double xMin = side.Min(bounds => bounds.X);
        double yMin = side.Min(bounds => bounds.Y);
        double xMax = side.Max(bounds => bounds.X + bounds.Width);
        double yMax = side.Max(bounds => bounds.Y + bounds.Height);
        double extent = Math.Max(xMax - xMin, yMax - yMin);
        double minimum = Math.Max(
            StructuralSideMinimumReferenceExtentRatio * reference,
            StructuralSideMinimumBarExtentRatio * bar.Width);
        return extent >= minimum;
    }

    private static double XOverlapRatio(InkBounds a, InkBounds b)
    {
        double overlap = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        double narrower = Math.Min(a.Width, b.Width);
        return narrower <= 0 ? 0 : Math.Max(0, overlap) / narrower;
    }

    private readonly record struct StrokeItem(Stroke Stroke, InkBounds Box);
}
