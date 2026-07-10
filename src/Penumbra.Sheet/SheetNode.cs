using Penumbra.Cas;
using Penumbra.Core;

namespace Penumbra.Sheet;

/// <summary>
/// One live cell of the reactive sheet: a recognized expression region wrapped as a dependency-graph
/// node. Keyed by an external region id (Seam 1), it carries the LaTeX + its analysed role, the Seam-1
/// linkage (tokens + bounds, empty in headless use), the latest <see cref="EvaluationResult"/>, and its
/// edges. Mutable by design — <see cref="SheetGraph"/> owns and mutates it in place across recomputes.
/// </summary>
public sealed class SheetNode
{
    internal SheetNode(Guid id, long insertionIndex)
    {
        Id = id;
        InsertionIndex = insertionIndex;
        Latex = string.Empty;
        FreeVariables = EmptyVars;
        Tokens = Array.Empty<RecognizedToken>();
    }

    private static readonly IReadOnlySet<string> EmptyVars = new HashSet<string>();

    /// <summary>Stable external key (the Seam-1 region id).</summary>
    public Guid Id { get; }

    /// <summary>The expression's LaTeX. Changed only through <see cref="SheetGraph.Upsert"/>.</summary>
    public string Latex { get; internal set; }

    /// <summary>What this node does in the sheet, from its analysis.</summary>
    public NodeRole Role { get; internal set; } = NodeRole.Statement;

    /// <summary>The symbol this node binds, when it is a <see cref="NodeRole.Definition"/>; else <c>null</c>.</summary>
    public string? DefinedSymbol { get; internal set; }

    /// <summary>The symbols this node depends on (its inbound edges' variables).</summary>
    public IReadOnlySet<string> FreeVariables { get; internal set; }

    /// <summary>Seam-1 tokens that formed this expression. Empty in headless use.</summary>
    public IReadOnlyList<RecognizedToken> Tokens { get; internal set; }

    /// <summary>Seam-1 ink bounds of the region. Absent in headless use.</summary>
    public InkBounds? Region { get; internal set; }

    /// <summary>The latest evaluation, or <c>null</c> before the first recompute.</summary>
    public EvaluationResult? Result { get; internal set; }

    /// <summary>True when this node re-defines a symbol another (winning) node already defines.</summary>
    public bool IsConflict { get; internal set; }

    /// <summary>Set when the node's LaTeX changed since the last recompute; drives incremental re-eval.</summary>
    public bool Dirty { get; internal set; }

    /// <summary>Insertion tie-breaker for duplicate-definition resolution when nodes have no region.</summary>
    internal long InsertionIndex { get; }

    /// <summary>Ids of nodes this node depends on (resolved from <see cref="FreeVariables"/>).</summary>
    internal HashSet<Guid> DependsOn { get; } = new();

    /// <summary>Ids of nodes that depend on this one — the inverse of <see cref="DependsOn"/>.</summary>
    internal HashSet<Guid> Dependents { get; } = new();
}
