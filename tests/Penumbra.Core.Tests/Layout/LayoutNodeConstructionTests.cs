using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Core.Tests.Layout;

/// <summary>
/// Construction invariants: nodes reject null children/tokens and structurally empty shapes at the door, so a
/// malformed tree can never exist to be validated or serialized.
/// </summary>
public sealed class LayoutNodeConstructionTests
{
    private static RecognizedToken AnyToken() => LayoutTestFactory.Tok("x");

    [Fact]
    public void Leaf_RejectsNullToken() =>
        Assert.Throws<ArgumentNullException>(() => new LeafNode(null!));

    [Fact]
    public void Sequence_RejectsNullList() =>
        Assert.Throws<ArgumentNullException>(() => new SequenceNode(null!));

    [Fact]
    public void Sequence_RejectsEmpty() =>
        Assert.Throws<ArgumentException>(() => new SequenceNode(Array.Empty<LayoutNode>()));

    [Fact]
    public void Sequence_RejectsNullChild() =>
        Assert.Throws<ArgumentNullException>(() => new SequenceNode(new LayoutNode[] { null! }));

    [Fact]
    public void ImplicitProduct_RejectsFewerThanTwoFactors() =>
        Assert.Throws<ArgumentException>(() => new ImplicitProductNode(new LayoutNode[] { LayoutTestFactory.Leaf("2") }));

    [Fact]
    public void ImplicitProduct_RejectsNullFactor() =>
        Assert.Throws<ArgumentNullException>(() =>
            new ImplicitProductNode(new LayoutNode[] { LayoutTestFactory.Leaf("2"), null! }));

    [Fact]
    public void Script_RejectsNullBase() =>
        Assert.Throws<ArgumentNullException>(() => new ScriptNode(null!, LayoutTestFactory.Leaf("2"), null));

    [Fact]
    public void Script_RejectsBothSlotsNull() =>
        Assert.Throws<ArgumentException>(() => new ScriptNode(LayoutTestFactory.Leaf("x"), null, null));

    [Fact]
    public void Script_AllowsSuperscriptOnly() =>
        Assert.NotNull(new ScriptNode(LayoutTestFactory.Leaf("x"), LayoutTestFactory.Leaf("2"), null));

    [Fact]
    public void Script_AllowsSubscriptOnly() =>
        Assert.NotNull(new ScriptNode(LayoutTestFactory.Leaf("x"), null, LayoutTestFactory.Leaf("1")));

    [Fact]
    public void Fraction_RejectsNullNumerator() =>
        Assert.Throws<ArgumentNullException>(() =>
            new FractionNode(null!, LayoutTestFactory.Leaf("2"), AnyToken()));

    [Fact]
    public void Fraction_RejectsNullDenominator() =>
        Assert.Throws<ArgumentNullException>(() =>
            new FractionNode(LayoutTestFactory.Leaf("1"), null!, AnyToken()));

    [Fact]
    public void Fraction_RejectsNullBar() =>
        Assert.Throws<ArgumentNullException>(() =>
            new FractionNode(LayoutTestFactory.Leaf("1"), LayoutTestFactory.Leaf("2"), null!));

    [Fact]
    public void Radical_RejectsNullRadicand() =>
        Assert.Throws<ArgumentNullException>(() => new RadicalNode(null!, null, AnyToken()));

    [Fact]
    public void Radical_RejectsNullMark() =>
        Assert.Throws<ArgumentNullException>(() => new RadicalNode(LayoutTestFactory.Leaf("9"), null, null!));

    [Fact]
    public void Radical_AllowsNullRootIndex() =>
        Assert.Null(new RadicalNode(LayoutTestFactory.Leaf("9"), null, AnyToken()).RootIndex);

    [Fact]
    public void Group_RejectsNullInner() =>
        Assert.Throws<ArgumentNullException>(() =>
            new DelimitedGroupNode(null!, LayoutTestFactory.Tok("("), LayoutTestFactory.Tok(")")));

    [Fact]
    public void Group_RejectsNullOpen() =>
        Assert.Throws<ArgumentNullException>(() =>
            new DelimitedGroupNode(LayoutTestFactory.Leaf("x"), null!, LayoutTestFactory.Tok(")")));

    [Fact]
    public void Group_RejectsNullClose() =>
        Assert.Throws<ArgumentNullException>(() =>
            new DelimitedGroupNode(LayoutTestFactory.Leaf("x"), LayoutTestFactory.Tok("("), null!));

    [Fact]
    public void Function_RejectsBlankName() =>
        Assert.Throws<ArgumentException>(() =>
            new FunctionCallNode("  ", new[] { AnyToken() }, LayoutTestFactory.Leaf("x")));

    [Fact]
    public void Function_RejectsEmptyNameTokens() =>
        Assert.Throws<ArgumentException>(() =>
            new FunctionCallNode("sin", Array.Empty<RecognizedToken>(), LayoutTestFactory.Leaf("x")));

    [Fact]
    public void Function_RejectsNullNameToken() =>
        Assert.Throws<ArgumentNullException>(() =>
            new FunctionCallNode("sin", new RecognizedToken[] { null! }, LayoutTestFactory.Leaf("x")));

    [Fact]
    public void Function_RejectsNullArgument() =>
        Assert.Throws<ArgumentNullException>(() =>
            new FunctionCallNode("sin", new[] { AnyToken() }, null!));

    [Fact]
    public void Relation_RejectsNullLeft() =>
        Assert.Throws<ArgumentNullException>(() => new RelationNode(null!, AnyToken(), LayoutTestFactory.Leaf("5")));

    [Fact]
    public void Relation_RejectsNullSign() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RelationNode(LayoutTestFactory.Leaf("x"), null!, LayoutTestFactory.Leaf("5")));

    [Fact]
    public void Relation_AllowsNullRight_ForTrailingQuery() =>
        Assert.Null(new RelationNode(LayoutTestFactory.Leaf("x"), AnyToken(), null).Right);

    [Fact]
    public void Sequence_ExposesImmutableChildren_SnapshotOfInput()
    {
        var list = new List<LayoutNode> { LayoutTestFactory.Leaf("1"), LayoutTestFactory.Leaf("2") };
        var node = new SequenceNode(list);

        // Mutating the source after construction must not change the node.
        list.Add(LayoutTestFactory.Leaf("3"));

        Assert.Equal(2, node.Children.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<LayoutNode>)node.Children).Add(LayoutTestFactory.Leaf("4")));
    }
}
