using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Core.Tests.Layout;

/// <summary>
/// Builders for layout tokens/nodes. Each token gets a fresh unique stroke id unless one is supplied, so
/// ownership/disjointness tests can force overlaps or shared instances deliberately.
/// </summary>
internal static class LayoutTestFactory
{
    public static RecognizedToken Tok(string latex, bool rejected = false, params Guid[] strokes)
    {
        var ids = strokes.Length == 0 ? new[] { Guid.NewGuid() } : strokes;
        return new RecognizedToken(latex, ids, default, Confidence: 1.0, Rejected: rejected);
    }

    public static RecognizedToken Tok(string latex, params Guid[] strokes) => Tok(latex, false, strokes);

    public static LeafNode Leaf(string latex) => new(Tok(latex));

    public static LeafNode Leaf(RecognizedToken token) => new(token);

    public static SequenceNode Seq(params LayoutNode[] children) => new(children);

    public static ImplicitProductNode Product(params LayoutNode[] factors) => new(factors);

    public static ScriptNode Sup(LayoutNode @base, LayoutNode superscript) => new(@base, superscript, null);

    public static ScriptNode Sub(LayoutNode @base, LayoutNode subscript) => new(@base, null, subscript);

    public static DelimitedGroupNode Paren(LayoutNode inner) => new(inner, Tok("("), Tok(")"));

    public static RelationNode Eq(LayoutNode left, LayoutNode? right) => new(left, Tok("="), right);
}
