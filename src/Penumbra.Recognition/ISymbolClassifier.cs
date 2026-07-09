using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// The label + confidence a classifier assigns to one symbol's strokes. <c>Energy</c> is the free energy
/// <c>−T·logsumexp(logits / T)</c> (audit B4) and <c>Rejected</c> flags an out-of-distribution symbol whose
/// energy crossed the calibrated reject threshold. Classifiers without calibration (the test fakes) leave
/// both at their defaults — energy <c>0</c>, not rejected — so their construction sites need no change.
/// </summary>
public readonly record struct SymbolPrediction(
    string Label,
    double Confidence,
    double Energy = 0,
    bool Rejected = false);

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

    /// <summary>
    /// Classify a whole line's symbols (same <paramref name="context"/> for all — they are siblings on
    /// one line). The default loops <see cref="Classify"/>; implementations backed by a model with a
    /// dynamic batch axis override this with a single inference call (Phase 4.5a).
    /// </summary>
    IReadOnlyList<SymbolPrediction> ClassifyBatch(
        IReadOnlyList<IReadOnlyList<Stroke>> symbols, SymbolContext context)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        var predictions = new SymbolPrediction[symbols.Count];
        for (int i = 0; i < symbols.Count; i++)
        {
            predictions[i] = Classify(symbols[i], context);
        }

        return predictions;
    }
}
