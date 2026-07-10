namespace Penumbra.Sheet;

/// <summary>What a <see cref="SheetNode"/> does in the sheet, derived from its expression analysis.</summary>
public enum NodeRole
{
    /// <summary>Binds a symbol to a value, e.g. <c>x = 5</c> or <c>y = x + 2</c>. Feeds dependents.</summary>
    Definition,

    /// <summary>A trailing-<c>=</c> compute request, e.g. <c>2 + x =</c>. Consumes; defines nothing.</summary>
    Query,

    /// <summary>Anything else — an equation to solve (<c>2x + 3 = 7</c>) or a bare expression.</summary>
    Statement,
}
