namespace Penumbra.Core;

/// <summary>The persisted Penumbra page model.</summary>
public sealed record PenumbraDocument(
    IReadOnlyList<Stroke> Strokes,
    IReadOnlyList<ExpressionNode> Expressions,
    IReadOnlyDictionary<string, string> Variables,
    int Version);
