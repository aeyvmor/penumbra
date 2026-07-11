using System.Reflection;
using Penumbra.Cas;

namespace Penumbra.Sheet.Tests;

/// <summary>
/// Headless proof of the non-mutating what-if API (Phase 5 increment 3, lane 1): <see cref="SheetGraph.Probe"/>
/// answers "what would the sheet show if this node were <c>trialLatex</c>?" with the same recompute
/// semantics as the committed path, while touching no node's result, dirty flag, conflict flag, or edges.
/// </summary>
public sealed class SheetGraphProbeTests
{
    private readonly CountingEvaluator _evaluator = new(new AngouriMathEvaluator());
    private readonly SheetGraph _graph;

    public SheetGraphProbeTests()
    {
        _graph = new SheetGraph(_evaluator, new AngouriMathExpressionAnalyzer());
    }

    private static Guid NewId() => Guid.NewGuid();

    private static SheetGraph NewRealGraph() =>
        new(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer());

    private static ProbeEntry Entry(SheetProbeReport report, Guid id) =>
        report.Entries.Single(e => e.Node.Id == id);

    // ---- the "done when" what-if chain --------------------------------------------------------

    [Fact]
    public void ProbeComputesTrialChainForDependents()
    {
        // x=5, y=x+2, y+1=  →  probing x→9 would make y=11 and the query 12, without committing anything.
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var yId = NewId();
        _graph.Upsert(yId, "y=x+2");
        var qId = NewId();
        _graph.Upsert(qId, "y+1=");
        _graph.Recompute();

        var report = _graph.Probe(xId, "x=9");

        Assert.Equal("9", Entry(report, xId).TrialResult.DisplayText);
        Assert.Equal("11", Entry(report, yId).TrialResult.DisplayText);
        Assert.Equal("12", Entry(report, qId).TrialResult.DisplayText);

        // The live sheet is unchanged — the probe was a hypothetical.
        Assert.Equal("5", _graph.Find(xId)!.Result!.DisplayText);
        Assert.Equal("7", _graph.Find(yId)!.Result!.DisplayText);
        Assert.Equal("8", _graph.Find(qId)!.Result!.DisplayText);
    }

    [Fact]
    public void ProbeReturnsEntriesInEvaluationOrder()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var yId = NewId();
        _graph.Upsert(yId, "y=x+2");
        var qId = NewId();
        _graph.Upsert(qId, "y+1=");
        _graph.Recompute();

        var report = _graph.Probe(xId, "x=9");

        // Plain chain: the probed node is the topological root, so it leads, then its dependents in order.
        Assert.Equal(new[] { xId, yId, qId }, report.Entries.Select(e => e.Node.Id).ToArray());
    }

    // ---- non-mutation (the whole point) -------------------------------------------------------

    [Fact]
    public void ProbeDoesNotMutateResultsDirtyConflictsOrEdges()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var dupId = NewId();
        _graph.Upsert(dupId, "x=9");   // duplicate definition → a conflict loser
        var yId = NewId();
        _graph.Upsert(yId, "y=x+1");
        var qId = NewId();
        _graph.Upsert(qId, "y+1=");
        _graph.Recompute();

        var before = Snapshot();
        _graph.Probe(xId, "x=100");
        var after = Snapshot();

        Assert.Equal(before, after);
    }

    [Fact]
    public void ProbeLeavesDirtyFlagsUntouched()
    {
        // Upsert without recompute leaves both nodes dirty (pending state).
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var yId = NewId();
        _graph.Upsert(yId, "y=x+2");
        Assert.True(_graph.Find(xId)!.Dirty);
        Assert.True(_graph.Find(yId)!.Dirty);

        _graph.Probe(xId, "x=9");

        // The probe must not evaluate-and-clear: dirty stays dirty...
        Assert.True(_graph.Find(xId)!.Dirty);
        Assert.True(_graph.Find(yId)!.Dirty);

        // ...and the deferred recompute then behaves exactly as if the probe never happened.
        _graph.Recompute();
        Assert.Equal("5", _graph.Find(xId)!.Result!.DisplayText);
        Assert.Equal("7", _graph.Find(yId)!.Result!.DisplayText);
    }

    [Fact]
    public void RecomputeAfterProbeMatchesRecomputeWithoutProbe()
    {
        var xId = NewId();
        var yId = NewId();
        var qId = NewId();

        var probed = NewRealGraph();
        var twin = NewRealGraph();
        foreach (var g in new[] { probed, twin })
        {
            g.Upsert(xId, "x=5");
            g.Upsert(yId, "y=x+2");
            g.Upsert(qId, "y+1=");
            g.Recompute();
        }

        probed.Probe(xId, "x=9");   // only this graph gets probed

        foreach (var g in new[] { probed, twin })
        {
            g.Upsert(xId, "x=9");
            g.Recompute();
        }

        // A probe leaves no residue: the committed edit lands identically on both graphs.
        Assert.Equal(twin.Find(xId)!.Result, probed.Find(xId)!.Result);
        Assert.Equal(twin.Find(yId)!.Result, probed.Find(yId)!.Result);
        Assert.Equal(twin.Find(qId)!.Result, probed.Find(qId)!.Result);
    }

    [Fact]
    public void RepeatedProbeIsIdempotent()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var yId = NewId();
        _graph.Upsert(yId, "y=x+2");
        _graph.Upsert(NewId(), "y+1=");
        _graph.Recompute();

        var first = _graph.Probe(xId, "x=9");
        var second = _graph.Probe(xId, "x=9");

        Assert.Equal(first.Entries.Count, second.Entries.Count);
        for (var i = 0; i < first.Entries.Count; i++)
        {
            Assert.Same(first.Entries[i].Node, second.Entries[i].Node);
            Assert.Equal(first.Entries[i].TrialResult, second.Entries[i].TrialResult);
        }
    }

    // ---- inherited semantics ------------------------------------------------------------------

    [Fact]
    public void ProbeConflictNodeYieldsConflictError()
    {
        var winnerId = NewId();
        _graph.Upsert(winnerId, "x=5");
        var loserId = NewId();
        _graph.Upsert(loserId, "x=9");   // loses to the first-inserted definer
        _graph.Recompute();

        // Probing the loser to another duplicate keeps it a conflict — no value, an error.
        var report = _graph.Probe(loserId, "x=8");

        var entry = Entry(report, loserId);
        Assert.Equal(EvaluationKind.Error, entry.TrialResult.Kind);
        Assert.False(entry.TrialResult.IsComputed);
        Assert.Contains("already defined", entry.TrialResult.DisplayText);
    }

    [Fact]
    public void ProbeCycleMembersYieldCycleError()
    {
        var aId = NewId();
        _graph.Upsert(aId, "a=1");
        var bId = NewId();
        _graph.Upsert(bId, "b=a+1");
        _graph.Recompute();
        _evaluator.Reset();

        // Probing a→"a=b+1" closes an a↔b loop: both are cycle members, both error, neither evaluates.
        var report = _graph.Probe(aId, "a=b+1");

        foreach (var id in new[] { aId, bId })
        {
            var entry = Entry(report, id);
            Assert.Equal(EvaluationKind.Error, entry.TrialResult.Kind);
            Assert.False(entry.TrialResult.IsComputed);
        }

        Assert.Equal(0, _evaluator.Calls); // cycle members are never handed to the evaluator
    }

    [Fact]
    public void ProbeUnboundVariableStaysSymbolic()
    {
        var nId = NewId();
        _graph.Upsert(nId, "1+1=");
        _graph.Recompute();

        // The trial references x, which nothing defines — the result stays honestly symbolic.
        var report = _graph.Probe(nId, "x+1=");

        var entry = Entry(report, nId);
        Assert.Equal(EvaluationKind.Symbolic, entry.TrialResult.Kind);
        Assert.Contains("x", entry.TrialResult.DisplayText);
    }

    [Fact]
    public void ProbeTrialChangingDefinedSymbolRebindsScratchOwners()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var yId = NewId();
        _graph.Upsert(yId, "y=x+2");
        var qId = NewId();
        _graph.Upsert(qId, "y+1=");
        _graph.Recompute();

        // Renaming x→z in the trial severs x's binding: y's old dependence on x goes symbolic again.
        var report = _graph.Probe(xId, "z=5");

        Assert.Equal("5", Entry(report, xId).TrialResult.DisplayText); // z=5 still evaluates its RHS
        var ey = Entry(report, yId);
        Assert.Equal(EvaluationKind.Symbolic, ey.TrialResult.Kind);
        Assert.Contains("x", ey.TrialResult.DisplayText);
        Assert.Contains(qId, report.Entries.Select(e => e.Node.Id)); // the downstream query is reseeded too
    }

    [Fact]
    public void ProbeOnDefinitionEvaluatesRightHandSide()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=1");
        _graph.Recompute();

        // A definition-role trial evaluates only its RHS ("2+3"), not the whole "x=2+3" as an equation.
        var report = _graph.Probe(xId, "x=2+3");

        Assert.Equal("5", Entry(report, xId).TrialResult.DisplayText);
        Assert.Equal(EvaluationKind.Number, Entry(report, xId).TrialResult.Kind);
    }

    [Fact]
    public void ProbeUnknownNodeThrows()
    {
        _graph.Upsert(NewId(), "x=5");
        _graph.Recompute();

        Assert.Throws<ArgumentException>(() => _graph.Probe(NewId(), "x=9"));
    }

    // ---- cost bound ---------------------------------------------------------------------------

    [Fact]
    public void ProbeEvaluatesOnlyAffectedSet_CountingEvaluator()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var yId = NewId();
        _graph.Upsert(yId, "y=x+2");
        var qId = NewId();
        _graph.Upsert(qId, "y+1=");
        _graph.Upsert(NewId(), "a=1");    // unrelated island — must never reach the evaluator
        _graph.Upsert(NewId(), "a+2=");
        _graph.Recompute();
        _evaluator.Reset();

        var report = _graph.Probe(xId, "x=9");

        // Exactly the probed node and its downstream cone are evaluated.
        Assert.Equal(3, _evaluator.Calls);
        Assert.Equal(
            new[] { xId, yId, qId }.ToHashSet(),
            report.Entries.Select(e => e.Node.Id).ToHashSet());
    }

    // ---- snapshot helpers (the Sheet test project has no InternalsVisibleTo) ------------------

    private List<NodeState> Snapshot() =>
        _graph.Nodes
            .OrderBy(n => n.Id)
            .Select(n => new NodeState(
                n.Id,
                n.Result,
                n.Dirty,
                n.IsConflict,
                EdgeString(InternalSet(n, "DependsOn")),
                EdgeString(InternalSet(n, "Dependents"))))
            .ToList();

    private static string EdgeString(HashSet<Guid> ids) => string.Join(",", ids.OrderBy(g => g));

    private static HashSet<Guid> InternalSet(SheetNode node, string propertyName)
    {
        var property = typeof(SheetNode).GetProperty(
            propertyName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (HashSet<Guid>)property.GetValue(node)!;
    }

    private sealed record NodeState(
        Guid Id,
        EvaluationResult? Result,
        bool Dirty,
        bool IsConflict,
        string DependsOn,
        string Dependents);
}
