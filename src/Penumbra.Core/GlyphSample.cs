namespace Penumbra.Core;

/// <summary>A captured handwritten exemplar for one output symbol.</summary>
public sealed record GlyphSample(
    string Symbol,
    IReadOnlyList<Stroke> Strokes,
    DateTimeOffset CapturedAt);
