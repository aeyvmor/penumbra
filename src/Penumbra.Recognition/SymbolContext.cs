using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Sibling context for a symbol's geometry features: the typical symbol height (<see cref="RefHeight"/>)
/// and the vertical extent of the line it sits in (<see cref="ExprYMin"/>, <see cref="ExprHeight"/>).
/// Mirrors the (ref_h, expr_ymin, expr_h) triple that <c>crohme.py</c>'s geom_features computes per
/// expression at training time — supplying it at run time is what lets size/baseline cues separate
/// e.g. <c>1</c> from <c>|</c>, or a low comma from a mid-line minus.
/// </summary>
public readonly record struct SymbolContext(double RefHeight, double ExprYMin, double ExprHeight)
{
    /// <summary>
    /// Context for a symbol judged on its own (no siblings): reference height = its own height, line
    /// extent = its own bounds. Yields rel_height≈1 and y_position≈0.5 — the neutral values the
    /// single-symbol path produced before segmentation existed.
    /// </summary>
    public static SymbolContext ForSelf(InkBounds bounds) =>
        new(bounds.Height, bounds.Y, bounds.Height);

    /// <inheritdoc cref="ForSelf(InkBounds)"/>
    public static SymbolContext ForSelf(IReadOnlyList<Stroke> strokes) =>
        ForSelf(SymbolPreprocessor.Bounds(strokes));
}
