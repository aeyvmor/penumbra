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

        IReadOnlyList<StrokeGroup> groups = _segmenter.Segment(strokes);
        if (groups.Count == 0)
        {
            return new RecognitionResult(string.Empty, Array.Empty<RecognizedToken>(), 0);
        }

        // Compute the line context once so every symbol is judged against the same neighbours —
        // this is where the geometry features finally get real siblings instead of self-as-context.
        SymbolContext context = LineContext(groups);

        var tokens = new List<RecognizedToken>(groups.Count);
        var latex = new StringBuilder();
        double confidenceSum = 0;

        foreach (StrokeGroup group in groups)
        {
            SymbolPrediction prediction = _classifier.Classify(group.Strokes, context);
            tokens.Add(new RecognizedToken(
                prediction.Label,
                group.Strokes.Select(s => s.Id).ToList(),
                group.Bounds,
                prediction.Confidence));
            latex.Append(prediction.Label);
            confidenceSum += prediction.Confidence;
        }

        return new RecognitionResult(latex.ToString(), tokens, confidenceSum / tokens.Count);
    }

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
}
