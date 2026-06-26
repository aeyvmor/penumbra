namespace Penumbra.Core;

/// <summary>A recognized symbol mapped back to its source strokes.</summary>
public sealed record RecognizedToken(
    string Latex,
    IReadOnlyList<Guid> SourceStrokeIds,
    InkBounds Bounds,
    double Confidence);
