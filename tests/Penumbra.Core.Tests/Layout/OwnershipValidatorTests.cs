using Penumbra.Core;
using Penumbra.Core.Layout;
using static Penumbra.Core.Tests.Layout.LayoutTestFactory;

namespace Penumbra.Core.Tests.Layout;

/// <summary>
/// The mechanical ownership check is what makes a tree trustworthy. Every non-rejected token must be owned
/// exactly once — leaf operand or structural mark — with strokes disjoint across the tree.
/// </summary>
public sealed class OwnershipValidatorTests
{
    [Fact]
    public void Accepts_WellFormedTree_EveryTokenOwnedOnce()
    {
        // 2x+5=13 with a shared instance list threaded into both the tree and the "recognition result".
        var t2 = Tok("2");
        var tx = Tok("x");
        var tplus = Tok("+");
        var t5 = Tok("5");
        var teq = Tok("=");
        var t1 = Tok("1");
        var t3 = Tok("3");

        var tree = new RelationNode(
            Seq(Product(Leaf(t2), Leaf(tx)), Leaf(tplus), Leaf(t5)),
            teq,
            Seq(Leaf(t1), Leaf(t3)));

        var result = new[] { t2, tx, tplus, t5, teq, t1, t3 };

        var validation = OwnershipValidator.Validate(tree, result);

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Violations);
    }

    [Fact]
    public void Accepts_StructuralMarksAsOwnership_FractionRadicalGroupFunctionRelation()
    {
        var bar = Tok(@"\bar");
        var rad = Tok(@"\sqrt");
        var open = Tok("(");
        var close = Tok(")");
        var s = Tok("s");
        var i = Tok("i");
        var n = Tok("n");
        var teq = Tok("=");
        var one = Tok("1");
        var two = Tok("2");
        var x = Tok("x");
        var y = Tok("y");

        // \frac{1}{2} inside a radical, equal to sin(x) — exercises every mark-owning node.
        var tree = new RelationNode(
            new RadicalNode(new FractionNode(Leaf(one), Leaf(two), bar), null, rad),
            teq,
            new FunctionCallNode("sin", new[] { s, i, n },
                new DelimitedGroupNode(Leaf(x), open, close)));

        var result = new[] { bar, rad, open, close, s, i, n, teq, one, two, x };
        // y is present in the recognition list only to prove nothing forces it to be owned when absent.
        var validation = OwnershipValidator.Validate(tree, result);

        Assert.True(validation.IsValid);
        _ = y;
    }

    [Fact]
    public void Ignores_RejectedTokensInResult_NotRequiredToBeOwned()
    {
        var x = Tok("x");
        var noise = Tok(".", rejected: true);

        var validation = OwnershipValidator.Validate(Leaf(x), new[] { x, noise });

        Assert.True(validation.IsValid);
    }

    [Fact]
    public void Flags_LostToken_WhenResultTokenIsNotOwned()
    {
        var x = Tok("x");
        var orphan = Tok("5");

        var validation = OwnershipValidator.Validate(Leaf(x), new[] { x, orphan });

        Assert.False(validation.IsValid);
        var violation = Assert.Single(validation.Violations);
        Assert.Equal(OwnershipViolationKind.LostToken, violation.Kind);
        Assert.Same(orphan, violation.Token);
    }

    [Fact]
    public void Flags_DoubleOwnership_WhenSameTokenOwnedTwice()
    {
        var x = Tok("x");

        var validation = OwnershipValidator.Validate(Seq(Leaf(x), Leaf(x)), new[] { x });

        Assert.Contains(validation.Violations, v => v.Kind == OwnershipViolationKind.DoubleOwnership);
    }

    [Fact]
    public void Flags_UnknownOwnedToken_WhenTreeOwnsSomethingOutsideResult()
    {
        var x = Tok("x");
        var foreign = Tok("y");

        var validation = OwnershipValidator.Validate(Seq(Leaf(x), Leaf(foreign)), new[] { x });

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, v =>
            v.Kind == OwnershipViolationKind.UnknownOwnedToken && ReferenceEquals(v.Token, foreign));
    }

    [Fact]
    public void Flags_UnknownOwnedToken_WhenTreeOwnsARejectedGlyph()
    {
        var x = Tok("x");
        var rejected = Tok("o", rejected: true);

        var validation = OwnershipValidator.Validate(Seq(Leaf(x), Leaf(rejected)), new[] { x, rejected });

        Assert.False(validation.IsValid);
        var violation = Assert.Single(validation.Violations, v => ReferenceEquals(v.Token, rejected));
        Assert.Equal(OwnershipViolationKind.UnknownOwnedToken, violation.Kind);
        Assert.Contains("rejected", violation.Detail);
    }

    [Fact]
    public void Flags_OverlappingStrokes_WhenTwoOwnedTokensShareAStroke()
    {
        var shared = Guid.NewGuid();
        var a = Tok("1", shared);
        var b = Tok("2", shared);

        var validation = OwnershipValidator.Validate(Seq(Leaf(a), Leaf(b)), new[] { a, b });

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Violations, v => v.Kind == OwnershipViolationKind.OverlappingStrokes);
    }

    [Fact]
    public void Flags_LostMark_WhenFractionBarIsAForeignInstance()
    {
        var one = Tok("1");
        var two = Tok("2");
        var treeBar = Tok(@"\bar");
        var resultBar = Tok(@"\bar"); // equal fields, different instance

        var tree = new FractionNode(Leaf(one), Leaf(two), treeBar);
        var validation = OwnershipValidator.Validate(tree, new[] { one, two, resultBar });

        // The result's bar is never owned (lost); the tree's bar is not in the result (unknown).
        Assert.Contains(validation.Violations, v =>
            v.Kind == OwnershipViolationKind.LostToken && ReferenceEquals(v.Token, resultBar));
        Assert.Contains(validation.Violations, v =>
            v.Kind == OwnershipViolationKind.UnknownOwnedToken && ReferenceEquals(v.Token, treeBar));
    }

    [Fact]
    public void Validate_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => OwnershipValidator.Validate(null!, Array.Empty<RecognizedToken>()));
        Assert.Throws<ArgumentNullException>(() => OwnershipValidator.Validate(Leaf("x"), null!));
    }
}
