namespace Penumbra.Core;

/// <summary>A dependency-graph node for a recognized expression region.</summary>
public sealed record ExpressionNode(
    Guid Id,
    IReadOnlyList<RecognizedToken> Tokens,
    string Latex,
    IReadOnlyList<Guid> DependsOn,
    InkBounds Region);
