namespace Penumbra.Core;

/// <summary>The persisted Penumbra page model.</summary>
/// <remarks>
/// Earlier schemas carried an always-empty <c>Expressions</c> list (the placeholder
/// <c>ExpressionNode</c>, removed in Phase 5 — Seam 2's node now lives in <c>Penumbra.Sheet</c> as
/// <c>SheetNode</c>). Old files with <c>"Expressions": []</c> still load: the unknown property is
/// ignored. Sheet/graph state will persist in schema v3 (Phase 5 increment 2).
/// </remarks>
public sealed record PenumbraDocument(
    IReadOnlyList<Stroke> Strokes,
    IReadOnlyDictionary<string, string> Variables,
    int Version);
