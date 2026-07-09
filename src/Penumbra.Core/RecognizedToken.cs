namespace Penumbra.Core;

/// <summary>
/// A recognized symbol mapped back to its source strokes. <c>Rejected</c> marks a symbol the classifier's
/// energy-score calibration flagged as out-of-distribution (audit B4) — the reject gate treats it as
/// uncertain no matter how high its <c>Confidence</c>. Defaults to false so recognizers without
/// calibration (fakes, NoOp, pre-calibration models) are unchanged.
/// </summary>
public sealed record RecognizedToken(
    string Latex,
    IReadOnlyList<Guid> SourceStrokeIds,
    InkBounds Bounds,
    double Confidence,
    bool Rejected = false);
