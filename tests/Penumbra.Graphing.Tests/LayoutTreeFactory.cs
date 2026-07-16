using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Graphing.Tests;

/// <summary>
/// Builders for layout tokens/nodes, mirroring <c>Penumbra.Core.Tests.Layout.LayoutTestFactory</c>. That
/// factory is internal to <c>Penumbra.Core.Tests</c> and cannot be shared across assemblies, so the small
/// handful of helpers this project needs are reconstructed here from the same public
/// <c>Penumbra.Core.Layout</c> constructors the real grammar parser will eventually use.
/// </summary>
internal static class LayoutTreeFactory
{
    public static RecognizedToken Tok(string latex) => new(latex, new[] { Guid.NewGuid() }, default, Confidence: 1.0);

    public static LeafNode Leaf(string latex) => new(Tok(latex));

    public static SequenceNode Seq(params LayoutNode[] children) => new(children);

    public static ImplicitProductNode Product(params LayoutNode[] factors) => new(factors);

    public static ScriptNode Sup(LayoutNode @base, LayoutNode superscript) => new(@base, superscript, null);

    public static RelationNode Eq(LayoutNode left, LayoutNode? right) => new(left, Tok("="), right);

    public static RelationNode Relation(LayoutNode left, string relationLatex, LayoutNode? right) =>
        new(left, Tok(relationLatex), right);

    public static FunctionCallNode FunctionCall(string name, LayoutNode argument) =>
        new(name, name.Select(c => Tok(c.ToString())).ToArray(), argument);
}
