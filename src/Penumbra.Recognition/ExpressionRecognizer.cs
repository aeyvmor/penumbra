using System.Text;
using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// R1 line recognizer (Phase 3, Step 5): segments a page of strokes into symbols, classifies each in
/// the geometry context of its neighbours, and assembles the labels left-to-right into LaTeX. This
/// fully realizes Seam 1 — every <see cref="RecognizedToken"/> carries the strokes that formed it.
///
/// M1 grammar is intentionally linear (concatenate ordered tokens). 2-D structure — superscripts,
/// subscripts, fractions, a <c>\sqrt</c> covering its radicand — is the spatial-grammar follow-up.
/// </summary>
public sealed class ExpressionRecognizer : IRecognizer
{
    private readonly IStrokeSegmenter _segmenter;
    private readonly ISymbolClassifier _classifier;

    public ExpressionRecognizer(IStrokeSegmenter segmenter, ISymbolClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        ArgumentNullException.ThrowIfNull(classifier);
        _segmenter = segmenter;
        _classifier = classifier;
    }

    /// <inheritdoc />
    public RecognitionResult Recognize(IReadOnlyList<Stroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        IReadOnlyList<StrokeGroup> allGroups = _segmenter.Segment(strokes);
        if (allGroups.Count == 0)
        {
            return new RecognitionResult(string.Empty, Array.Empty<RecognizedToken>(), 0, 0);
        }

        // 3.9f: split the page into horizontal lines by Y-projection and recognize only ONE line, so
        // a stray mark far above/below the writing can't stretch the line geometry (ref_h / expr_ymin
        // / expr_h) that every symbol on the real line is judged against.
        IReadOnlyList<StrokeGroup> groups = SelectLine(allGroups);

        // Compute the line context once so every symbol is judged against the same neighbours —
        // this is where the geometry features finally get real siblings instead of self-as-context.
        // Computed over the SELECTED line only: that per-line scope is the actual stray-mark guard.
        SymbolContext context = LineContext(groups);

        var predictions = new SymbolPrediction[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            predictions[i] = _classifier.Classify(groups[i].Strokes, context);
        }

        // 3.9b: correct the digit-context glyph confusions before assembly, so both the emitted
        // LaTeX and the Seam-1 token labels carry the geometry-aware reading.
        string[] labels = RewriteDigitContext(predictions);

        var tokens = new List<RecognizedToken>(groups.Count);
        var latex = new StringBuilder();
        double confidenceSum = 0;
        double minConfidence = double.PositiveInfinity;

        for (int i = 0; i < groups.Count; i++)
        {
            StrokeGroup group = groups[i];
            string label = labels[i];
            tokens.Add(new RecognizedToken(
                label,
                group.Strokes.Select(s => s.Id).ToList(),
                group.Bounds,
                predictions[i].Confidence));

            // 3.9a: a control word ("\pi", "\times", …) needs a trailing separator, else "\pi"
            // followed by "x" assembles to "\pix" — a phantom variable the translator reads as
            // "pix". Digits and letters stay directly concatenated (multi-digit numbers depend on
            // it: "2""1" must be 21, not a spaced "2 1" the translator would turn into 2*1).
            latex.Append(label);
            if (label.StartsWith('\\'))
            {
                latex.Append(' ');
            }

            confidenceSum += predictions[i].Confidence;
            minConfidence = Math.Min(minConfidence, predictions[i].Confidence);
        }

        return new RecognitionResult(latex.ToString().TrimEnd(), tokens, confidenceSum / groups.Count, minConfidence);
    }

    // 3.9b/3.9g: glyph confusions the classifier can't resolve from shape alone. Two distinct rules:
    //
    //   x → \times : context-dependent. A stray 'x' is the multiplication cross ONLY between two
    //     digits ("3x7" → "3\times 7"); flanked by an operator/relation or at a sequence edge it is
    //     an algebra variable and is left alone.
    //
    //   | → 1 : UNCONDITIONAL (3.9g). '|' has no valid reading in the M1 linear-arithmetic grammar —
    //     absolute-value bars, norms, set-builder pipes etc. are far-future — so a classified '|' is
    //     always either a drawn '1' or garbage, and culling garbage is the confidence gate's job, not
    //     the rewriter's. The old rule only relabelled '|' when BOTH neighbours were values, so real
    //     ink like '2|+7=' (digit on the left, operator on the right) and '4|+9=' fell through and
    //     the translator died on the raw '|' with a cryptic "mismatched input ')'". Rewriting every
    //     '|' — including at sequence start/end — removes that whole failure class.
    private static string[] RewriteDigitContext(IReadOnlyList<SymbolPrediction> predictions)
    {
        var labels = new string[predictions.Count];
        for (int i = 0; i < predictions.Count; i++)
        {
            labels[i] = predictions[i].Label;
        }

        // Neighbour tests read the original labels so a rewrite never cascades into the next.
        var rewritten = (string[])labels.Clone();
        for (int i = 0; i < labels.Length; i++)
        {
            if (labels[i] == "|")
            {
                rewritten[i] = "1";
            }
            else if (labels[i] == "x"
                     && i > 0 && i < labels.Length - 1
                     && IsDigit(labels[i - 1]) && IsDigit(labels[i + 1]))
            {
                rewritten[i] = @"\times";
            }
        }

        return rewritten;
    }

    private static bool IsDigit(string label) => label.Length == 1 && char.IsAsciiDigit(label[0]);

    // The line's reference height + vertical extent, mirroring crohme.py build_split():
    // ref_h = median symbol height; expr_ymin / expr_h = the line's top edge and total height.
    private static SymbolContext LineContext(IReadOnlyList<StrokeGroup> groups)
    {
        var heights = groups.Select(g => g.Bounds.Height).OrderBy(h => h).ToList();
        int mid = heights.Count / 2;
        double median = heights.Count % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2.0;
        double refHeight = median > 0 ? median : 1.0;

        double yMin = groups.Min(g => g.Bounds.Y);
        double yMax = groups.Max(g => g.Bounds.Y + g.Bounds.Height);
        return new SymbolContext(refHeight, yMin, yMax - yMin);
    }

    // 3.9f: pick the single line to recognize. Labels aren't known yet (classification runs next),
    // so we can't yet prefer "the line containing the trailing '='" — that '=' refinement is a
    // spatial-grammar follow-up. For now take the largest line (most stroke groups), tie-broken by
    // widest X-extent, and return it in left-to-right order for assembly.
    private static IReadOnlyList<StrokeGroup> SelectLine(IReadOnlyList<StrokeGroup> groups)
    {
        List<List<StrokeGroup>> lines = SplitIntoLines(groups);

        List<StrokeGroup> chosen = lines
            .OrderByDescending(line => line.Count)
            .ThenByDescending(XExtent)
            .First();

        // Restore the segmenter's reading order (left-to-right, top-to-bottom) within the line.
        return chosen.OrderBy(g => g.Bounds.X).ThenBy(g => g.Bounds.Y).ToList();
    }

    // Group stroke boxes into horizontal lines by Y-projection: order by vertical center, then cut a
    // new line wherever the vertical gap down to the current line's lower edge exceeds ~0.8x the
    // page's median symbol height. The page median is a stable scale (robust to one stray mark);
    // 0.8x sits comfortably above intra-line baseline jitter yet below normal line spacing.
    private static List<List<StrokeGroup>> SplitIntoLines(IReadOnlyList<StrokeGroup> groups)
    {
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

    private static double XExtent(IReadOnlyList<StrokeGroup> line) =>
        line.Max(g => g.Bounds.X + g.Bounds.Width) - line.Min(g => g.Bounds.X);
}
