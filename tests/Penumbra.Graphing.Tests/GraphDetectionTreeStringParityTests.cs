using Penumbra.Core.Layout;
using static Penumbra.Graphing.Tests.LayoutTreeFactory;

namespace Penumbra.Graphing.Tests;

/// <summary>
/// Proves the parity contract from the Phase 6 kickoff: the LayoutNode tree path (what recognized ink will
/// eventually supply) and the LaTeX string path (typed fixtures / the Sheet's stored LaTeX) agree — same
/// candidate fields, same sampled series — for the same underlying expression.
/// </summary>
public sealed class GraphDetectionTreeStringParityTests
{
    private static readonly GraphDetector Detector = new();
    private static readonly DomainSampler Sampler = new();

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { Eq(Leaf("y"), Leaf("x")), "y=x" };
        yield return new object[] { Eq(Leaf("y"), Sup(Leaf("x"), Leaf("2"))), "y=x^2" };
        yield return new object[] { Eq(Leaf("y"), FunctionCall("sin", Leaf("x"))), @"y=\sin(x)" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TreeAndStringPaths_ProduceIdenticalCandidates(LayoutNode tree, string latex)
    {
        var fromTree = Detector.Detect(tree);
        var fromString = Detector.Detect(latex);

        Assert.True(fromTree.IsAccepted);
        Assert.True(fromString.IsAccepted);
        Assert.Equal(fromString.Candidate!.DependentVariable, fromTree.Candidate!.DependentVariable);
        Assert.Equal(fromString.Candidate.IndependentVariable, fromTree.Candidate.IndependentVariable);

        // ExpressionLatex is deliberately NOT asserted byte-identical here: LayoutLatexSerializer always
        // braces a script ("x^{2}"), while a typed fixture may type it bare ("x^2") — both are the same
        // dialect the translator accepts, just not the same bytes. The real parity claim is semantic, proven
        // by TreeAndStringPaths_ProduceIdenticalSeries below.
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TreeAndStringPaths_ProduceIdenticalSeries(LayoutNode tree, string latex)
    {
        var domain = GraphDomain.Create(-4, 4);

        var treeCandidate = Detector.Detect(tree).Candidate!;
        var stringCandidate = Detector.Detect(latex).Candidate!;

        var treeSeries = Sampler.SampleSeries(treeCandidate, domain, 17);
        var stringSeries = Sampler.SampleSeries(stringCandidate, domain, 17);

        Assert.True(treeSeries.IsSampled);
        Assert.True(stringSeries.IsSampled);
        Assert.Equal(stringSeries.Series, treeSeries.Series);
    }
}
