using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>The label + confidence a classifier assigns to one symbol's strokes.</summary>
public readonly record struct SymbolPrediction(string Label, double Confidence);

/// <summary>
/// Classifies a single segmented symbol's strokes (one <see cref="StrokeGroup"/>) into a label.
/// Segmentation and assembly live in <see cref="ExpressionRecognizer"/>; this is just the per-symbol
/// model call.
/// </summary>
public interface ISymbolClassifier
{
    /// <summary>
    /// Classify one symbol's strokes, using <paramref name="context"/> for the sibling-relative
    /// geometry features.
    /// </summary>
    SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context);
}
