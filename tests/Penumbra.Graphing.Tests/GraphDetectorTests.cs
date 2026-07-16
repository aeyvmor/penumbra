using static Penumbra.Graphing.Tests.LayoutTreeFactory;

namespace Penumbra.Graphing.Tests;

public sealed class GraphDetectorTests
{
    private static readonly GraphDetector Detector = new();

    // ---- accepted: string path ------------------------------------------------------------------------

    [Theory]
    [InlineData("y=x", "y", "x", "x")]
    [InlineData("y=x^2", "y", "x", "x^2")]
    [InlineData("y=2x+1", "y", "x", "2x+1")]
    [InlineData("f=x^2", "f", "x", "x^2")] // variable-name policy: LHS is not hardcoded to "y"
    public void Detect_String_AcceptsExplicitFunctions(
        string latex, string dependent, string independent, string expressionLatex)
    {
        var outcome = Detector.Detect(latex);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(dependent, outcome.Candidate!.DependentVariable);
        Assert.Equal(independent, outcome.Candidate.IndependentVariable);
        Assert.Equal(expressionLatex, outcome.Candidate.ExpressionLatex);
    }

    [Fact]
    public void Detect_String_AcceptsSin()
    {
        var outcome = Detector.Detect(@"y=\sin(x)");

        Assert.True(outcome.IsAccepted);
        Assert.Equal("y", outcome.Candidate!.DependentVariable);
        Assert.Equal("x", outcome.Candidate.IndependentVariable);
        Assert.Equal(@"\sin(x)", outcome.Candidate.ExpressionLatex);
    }

    [Fact]
    public void Detect_String_ConstantMultiplierPiDoesNotCountAsIndependentVariable()
    {
        // pi is a constant, not a free variable — "x" remains the only independent variable.
        var outcome = Detector.Detect(@"y=\pi x");

        Assert.True(outcome.IsAccepted);
        Assert.Equal("x", outcome.Candidate!.IndependentVariable);
    }

    // ---- rejected: string path -------------------------------------------------------------------------

    [Fact]
    public void Detect_String_RejectsNoRelation()
    {
        var outcome = Detector.Detect("x^2+1");

        Assert.False(outcome.IsAccepted);
        Assert.Equal(GraphRejectionReason.NotAnEquation, outcome.Reason);
    }

    [Fact]
    public void Detect_String_RejectsTrailingQuery()
    {
        var outcome = Detector.Detect("y=");

        Assert.Equal(GraphRejectionReason.NotAnEquation, outcome.Reason);
    }

    [Fact]
    public void Detect_String_RejectsInequality()
    {
        // \leq translates to "<=", never a bare top-level "=" — not graphable as an explicit function today.
        var outcome = Detector.Detect(@"y\leq x");

        Assert.Equal(GraphRejectionReason.NotAnEquation, outcome.Reason);
    }

    [Theory]
    [InlineData("2x=6")]
    [InlineData("x^2+y^2=1")]
    [InlineData("2+2=4")]
    public void Detect_String_RejectsLhsNotBareVariable(string latex)
    {
        var outcome = Detector.Detect(latex);

        Assert.Equal(GraphRejectionReason.LhsNotBareVariable, outcome.Reason);
    }

    [Theory]
    [InlineData("a=2")]
    [InlineData("a=\\pi")]
    public void Detect_String_RejectsConstantRhs(string latex)
    {
        var outcome = Detector.Detect(latex);

        Assert.Equal(GraphRejectionReason.ConstantRhs, outcome.Reason);
    }

    [Fact]
    public void Detect_String_RejectsMultipleFreeVariables()
    {
        var outcome = Detector.Detect("z=x+y");

        Assert.Equal(GraphRejectionReason.MultipleFreeVariables, outcome.Reason);
    }

    [Fact]
    public void Detect_String_RejectsChainedRelation()
    {
        var outcome = Detector.Detect("y=x=2");

        Assert.Equal(GraphRejectionReason.UnsupportedConstruct, outcome.Reason);
    }

    [Fact]
    public void Detect_String_RejectsSelfReferentialDependentVariable()
    {
        var outcome = Detector.Detect("x=x^2");

        Assert.Equal(GraphRejectionReason.UnsupportedConstruct, outcome.Reason);
    }

    [Fact]
    public void Detect_String_RejectsUnsupportedPlusMinusAsUnsupportedConstruct()
    {
        // \pm throws NotSupportedException inside the translator — an exception path, not a clean refusal.
        var outcome = Detector.Detect(@"y=x\pm1");

        Assert.Equal(GraphRejectionReason.UnsupportedConstruct, outcome.Reason);
    }

    [Fact]
    public void Detect_String_ThrowsOnNullLatex() =>
        Assert.Throws<ArgumentNullException>(() => Detector.Detect((string)null!));

    // ---- accepted: tree path ----------------------------------------------------------------------------

    [Fact]
    public void Detect_Tree_AcceptsLinear()
    {
        var tree = Eq(Leaf("y"), Leaf("x"));

        var outcome = Detector.Detect(tree);

        Assert.True(outcome.IsAccepted);
        Assert.Equal("y", outcome.Candidate!.DependentVariable);
        Assert.Equal("x", outcome.Candidate.IndependentVariable);
        Assert.Equal("x", outcome.Candidate.ExpressionLatex);
    }

    [Fact]
    public void Detect_Tree_AcceptsSquare()
    {
        var tree = Eq(Leaf("y"), Sup(Leaf("x"), Leaf("2")));

        var outcome = Detector.Detect(tree);

        Assert.True(outcome.IsAccepted);
        Assert.Equal("x^{2}", outcome.Candidate!.ExpressionLatex);
    }

    [Fact]
    public void Detect_Tree_AcceptsSin()
    {
        var tree = Eq(Leaf("y"), FunctionCall("sin", Leaf("x")));

        var outcome = Detector.Detect(tree);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(@"\sin(x)", outcome.Candidate!.ExpressionLatex);
    }

    // ---- rejected: tree path ----------------------------------------------------------------------------

    [Fact]
    public void Detect_Tree_RejectsNonRelationRoot()
    {
        var outcome = Detector.Detect(Leaf("x"));

        Assert.Equal(GraphRejectionReason.NotAnEquation, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_RejectsTrailingQuery()
    {
        var outcome = Detector.Detect(Eq(Leaf("y"), null));

        Assert.Equal(GraphRejectionReason.NotAnEquation, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_RejectsNonEqualsRelation()
    {
        var outcome = Detector.Detect(Relation(Leaf("y"), @"\leq", Leaf("x")));

        Assert.Equal(GraphRejectionReason.NotAnEquation, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_RejectsChainedRelation()
    {
        var outcome = Detector.Detect(Eq(Leaf("y"), Eq(Leaf("x"), Leaf("2"))));

        Assert.Equal(GraphRejectionReason.UnsupportedConstruct, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_RejectsLhsNotBareVariable()
    {
        var tree = Eq(Product(Leaf("2"), Leaf("x")), Leaf("6"));

        var outcome = Detector.Detect(tree);

        Assert.Equal(GraphRejectionReason.LhsNotBareVariable, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_RejectsConstantRhs()
    {
        var outcome = Detector.Detect(Eq(Leaf("a"), Leaf("2")));

        Assert.Equal(GraphRejectionReason.ConstantRhs, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_RejectsMultipleFreeVariables()
    {
        var tree = Eq(Leaf("z"), Seq(Leaf("x"), Leaf("+"), Leaf("y")));

        var outcome = Detector.Detect(tree);

        Assert.Equal(GraphRejectionReason.MultipleFreeVariables, outcome.Reason);
    }

    [Fact]
    public void Detect_Tree_ThrowsOnNullRoot() =>
        Assert.Throws<ArgumentNullException>(() => Detector.Detect((Penumbra.Core.Layout.LayoutNode)null!));
}
