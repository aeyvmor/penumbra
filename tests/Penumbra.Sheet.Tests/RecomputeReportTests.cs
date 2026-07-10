using Penumbra.Core;

namespace Penumbra.Sheet.Tests;

/// <summary>
/// Proves that result replacement and causal participation remain separate contracts.
/// </summary>
public sealed class RecomputeReportTests
{
    [Fact]
    public void EquivalentUpstreamEditAffectsChainWithoutReportingValueChanges()
    {
        var graph = NewGraph();
        Guid xId = Guid.NewGuid();
        SheetNode x = graph.Upsert(xId, "x=5");
        SheetNode y = graph.Upsert(Guid.NewGuid(), "y=x+2");
        SheetNode query = graph.Upsert(Guid.NewGuid(), "y+1=");
        graph.Recompute();

        graph.Upsert(xId, "x=2+3");
        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Empty(report.ChangedResultNodes);
        Assert.Equal(new[] { x.Id, y.Id, query.Id }, report.CausallyAffectedNodes.Select(n => n.Id));
    }

    [Fact]
    public void ChangedChainIsDependencyOrderedAndUnrelatedIslandIsAbsent()
    {
        var graph = NewGraph();
        Guid xId = Guid.NewGuid();
        SheetNode x = graph.Upsert(xId, "x=5");
        SheetNode y = graph.Upsert(Guid.NewGuid(), "y=x+2");
        SheetNode query = graph.Upsert(Guid.NewGuid(), "y+1=");
        SheetNode island = graph.Upsert(Guid.NewGuid(), "k=10");
        graph.Recompute();

        graph.Upsert(xId, "x=7");
        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Equal(new[] { x.Id, y.Id, query.Id }, report.CausallyAffectedNodes.Select(n => n.Id));
        Assert.DoesNotContain(island, report.CausallyAffectedNodes);
        Assert.Empty(graph.Recompute()); // compatibility wrapper observes no work once the graph is clean
    }

    [Fact]
    public void RemovalReportsSurvivingFormerDependentsButNotRemovedNode()
    {
        var graph = NewGraph();
        Guid xId = Guid.NewGuid();
        graph.Upsert(xId, "x=5");
        SheetNode y = graph.Upsert(Guid.NewGuid(), "y=x+2");
        SheetNode query = graph.Upsert(Guid.NewGuid(), "y+1=");
        graph.Recompute();

        Assert.True(graph.Remove(xId));
        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Equal(new[] { y.Id, query.Id }, report.CausallyAffectedNodes.Select(n => n.Id));
        Assert.DoesNotContain(report.CausallyAffectedNodes, n => n.Id == xId);
    }

    [Fact]
    public void RenameSeversOldEdgeAndStillRipplesThroughFormerDependents()
    {
        var graph = NewGraph();
        Guid definitionId = Guid.NewGuid();
        SheetNode definition = graph.Upsert(definitionId, "x=5");
        SheetNode direct = graph.Upsert(Guid.NewGuid(), "y=x+2");
        SheetNode downstream = graph.Upsert(Guid.NewGuid(), "y+1=");
        graph.Recompute();

        graph.Upsert(definitionId, "z=5");
        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Equal(
            new[] { definition.Id, direct.Id, downstream.Id },
            report.CausallyAffectedNodes.Select(n => n.Id));
        Assert.Contains(direct, report.ChangedResultNodes); // old x binding was honestly severed
    }

    [Fact]
    public void BreakingCycleReportsRecoveredMembersInDependencyOrder()
    {
        var graph = NewGraph();
        Guid aId = Guid.NewGuid();
        SheetNode a = graph.Upsert(aId, "a=b");
        SheetNode b = graph.Upsert(Guid.NewGuid(), "b=a");
        RecomputeReport cyclic = graph.RecomputeDetailed();
        Assert.Equal(new[] { a.Id, b.Id }, cyclic.CausallyAffectedNodes.Select(n => n.Id));

        graph.Upsert(aId, "a=1");
        RecomputeReport recovered = graph.RecomputeDetailed();

        Assert.Equal(new[] { a.Id, b.Id }, recovered.CausallyAffectedNodes.Select(n => n.Id));
    }

    [Fact]
    public void RegionOnlyOwnerMoveReportsBothDefinitionsAndDependent()
    {
        var graph = NewGraph();
        SheetNode first = graph.Upsert(
            Guid.NewGuid(), "x=5", region: new InkBounds(0, 10, 50, 20));
        Guid secondId = Guid.NewGuid();
        SheetNode second = graph.Upsert(
            secondId, "x=9", region: new InkBounds(0, 200, 50, 20));
        SheetNode query = graph.Upsert(Guid.NewGuid(), "x+1=");
        graph.Recompute();

        graph.Upsert(secondId, "x=9", region: new InkBounds(0, 0, 50, 20));
        RecomputeReport report = graph.RecomputeDetailed();

        Assert.Equal(
            new[] { first.Id, second.Id, query.Id },
            report.CausallyAffectedNodes.Select(n => n.Id));
        Assert.True(first.IsConflict);
        Assert.False(second.IsConflict);
    }

    private static SheetGraph NewGraph() =>
        new(new Penumbra.Cas.AngouriMathEvaluator(), new Penumbra.Cas.AngouriMathExpressionAnalyzer());
}
