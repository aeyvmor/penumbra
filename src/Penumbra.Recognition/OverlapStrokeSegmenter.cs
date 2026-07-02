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
            .Select(s => (Stroke: s, Box: SymbolPreprocessor.Bounds(new[] { s })))
            .ToList();
        if (items.Count == 0)
        {
            return Array.Empty<StrokeGroup>();
        }

        double reference = ReferenceSize(items.Select(i => i.Box));
        double gapX = _horizontalGapFactor * reference;
        double gapY = _verticalGapFactor * reference;

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
                if (Close(items[i].Box, items[j].Box, gapX, gapY))
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
}
