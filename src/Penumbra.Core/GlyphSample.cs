namespace Penumbra.Core;

/// <summary>A captured handwritten exemplar for one output symbol.</summary>
/// <param name="Symbol">The recognized label this exemplar was written for (e.g. "3", "\times").</param>
/// <param name="Strokes">The raw strokes that formed the glyph, stored exactly as captured.</param>
/// <param name="CapturedAt">When the sample was banked.</param>
/// <param name="DeviceClass">
/// The input device this ink came from ("mouse" / "pen" / "unknown"). Recorded now so the owned
/// corpus (ADR-0006) can stratify by device when synthesis and training arrive in Phase 4b.
/// </param>
/// <param name="ConsentToShare">
/// Whether the writer has opted this sample into the shareable corpus. Defaults to false — every
/// captured glyph is local-only until the user explicitly consents.
/// </param>
public sealed record GlyphSample(
    string Symbol,
    IReadOnlyList<Stroke> Strokes,
    DateTimeOffset CapturedAt,
    string DeviceClass = "unknown",
    bool ConsentToShare = false);
