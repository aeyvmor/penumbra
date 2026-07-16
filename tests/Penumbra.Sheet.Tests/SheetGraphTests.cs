using Penumbra.Cas;
using Penumbra.Core;

namespace Penumbra.Sheet.Tests;

/// <summary>
/// Headless proof of Seam 2 (Phase 5 increment 1, track B): the dependency graph pulls values through
/// the real CAS, re-evaluates exactly the dirty dependents on edit, and honours the kickoff's cycle
/// and duplicate-definition semantics — all with no ink and no UI.
/// </summary>
public sealed class SheetGraphTests
{
    private readonly CountingEvaluator _evaluator = new(new AngouriMathEvaluator());
    private readonly SheetGraph _graph;

    public SheetGraphTests()
    {
        _graph = new SheetGraph(_evaluator, new AngouriMathExpressionAnalyzer());
    }

    private static Guid NewId() => Guid.NewGuid();

    // ---- the Phase 5 "done when" chain -------------------------------------------------------

    [Fact]
    public void DefinitionChainEvaluatesEndToEnd()
    {
        // x=5, y=x+2, y+1=  →  x is 5, y is 7, the query answers 8.
        var x = _graph.Upsert(NewId(), "x=5");
        var y = _graph.Upsert(NewId(), "y=x+2");
        var q = _graph.Upsert(NewId(), "y+1=");

        var changed = _graph.Recompute();

        Assert.Equal("5", x.Result!.DisplayText);
        Assert.Equal("7", y.Result!.DisplayText);
        Assert.Equal("8", q.Result!.DisplayText);
        Assert.All(new[] { x, y, q }, n => Assert.True(n.Result!.IsComputed));
        Assert.Equal(3, changed.Count);
    }

    [Fact]
    public void RolesComeFromTheAnalyzer()
    {
        var x = _graph.Upsert(NewId(), "x=5");
        var q = _graph.Upsert(NewId(), "x+1=");
        var s = _graph.Upsert(NewId(), "2x+3=7");

        Assert.Equal(NodeRole.Definition, x.Role);
        Assert.Equal("x", x.DefinedSymbol);
        Assert.Equal(NodeRole.Query, q.Role);
        Assert.Equal(NodeRole.Statement, s.Role);
    }

    [Fact]
    public void EquationStatementSolvesWithUpstreamBindings()
    {
        // "x+y=10" with y defined elsewhere solves for x — bindings feed the solve.
        _graph.Upsert(NewId(), "y=3");
        var eq = _graph.Upsert(NewId(), "x+y=10");

        _graph.Recompute();

        Assert.Equal(EvaluationKind.Solution, eq.Result!.Kind);
        Assert.Equal("x = 7", eq.Result.DisplayText);
    }

    [Theory]
    [InlineData("2x=4", "2")]
    [InlineData(@"\frac{6}{3}x=5", "5/2")]
    public void UniqueSingleUnknownEquationFeedsLaterVariableQuery(
        string equation,
        string expected)
    {
        SheetNode statement = _graph.Upsert(NewId(), equation);
        SheetNode query = _graph.Upsert(NewId(), "x=");

        _graph.Recompute();

        Assert.Equal(NodeRole.Statement, statement.Role);
        Assert.Equal(EvaluationKind.Solution, statement.Result?.Kind);
        Assert.Equal(EvaluationKind.Number, query.Result?.Kind);
        Assert.Equal(expected, query.Result?.DisplayText);
    }

    [Fact]
    public void EditingEquationRecomputesItsDerivedVariableQuery()
    {
        Guid equationId = NewId();
        _graph.Upsert(equationId, "2x=4");
        SheetNode query = _graph.Upsert(NewId(), "x=");
        _graph.Recompute();
        Assert.Equal("2", query.Result?.DisplayText);

        _graph.Upsert(equationId, "2x=6");
        IReadOnlyList<SheetNode> changed = _graph.Recompute();

        Assert.Equal("3", query.Result?.DisplayText);
        Assert.Contains(query, changed);
    }

    [Fact]
    public void MultipleRootEquationDoesNotInventOneVariableBinding()
    {
        _graph.Upsert(NewId(), "x^2=4");
        SheetNode query = _graph.Upsert(NewId(), "x=");

        _graph.Recompute();

        Assert.Equal(EvaluationKind.Symbolic, query.Result?.Kind);
        Assert.Equal("x", query.Result?.DisplayText);
    }

    [Fact]
    public void MultipleEquationCandidatesDoNotSilentlyChooseOne()
    {
        _graph.Upsert(NewId(), "2x=4");
        _graph.Upsert(NewId(), "3x=6");
        SheetNode query = _graph.Upsert(NewId(), "x=");

        _graph.Recompute();

        Assert.Equal(EvaluationKind.Symbolic, query.Result?.Kind);
        Assert.Equal("x", query.Result?.DisplayText);
    }

    [Fact]
    public void ExplicitDefinitionWinsOverEquationDerivedBinding()
    {
        _graph.Upsert(NewId(), "2x=4");
        _graph.Upsert(NewId(), "x=5");
        SheetNode query = _graph.Upsert(NewId(), "x=");

        _graph.Recompute();

        Assert.Equal("5", query.Result?.DisplayText);
    }

    [Fact]
    public void AddingAndRemovingSecondEquationRevokesAndRestoresDerivedBinding()
    {
        _graph.Upsert(NewId(), "2x=4");
        SheetNode query = _graph.Upsert(NewId(), "x=");
        _graph.Recompute();
        Assert.Equal("2", query.Result?.DisplayText);

        Guid secondId = NewId();
        _graph.Upsert(secondId, "3x=6");
        _graph.Recompute();
        Assert.Equal(EvaluationKind.Symbolic, query.Result?.Kind);
        Assert.Equal("x", query.Result?.DisplayText);

        _graph.Remove(secondId);
        _graph.Recompute();
        Assert.Equal(EvaluationKind.Number, query.Result?.Kind);
        Assert.Equal("2", query.Result?.DisplayText);
    }

    [Fact]
    public void AddingExplicitDefinitionReplacesExistingDerivedBinding()
    {
        _graph.Upsert(NewId(), "2x=4");
        SheetNode query = _graph.Upsert(NewId(), "x=");
        _graph.Recompute();
        Assert.Equal("2", query.Result?.DisplayText);

        _graph.Upsert(NewId(), "x=5");
        _graph.Recompute();

        Assert.Equal("5", query.Result?.DisplayText);
    }

    // ---- incremental recompute ---------------------------------------------------------------

    [Fact]
    public void EditReEvaluatesExactlyTheDependentSet()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var y = _graph.Upsert(NewId(), "y=x+2");
        var q = _graph.Upsert(NewId(), "y+1=");
        var a = _graph.Upsert(NewId(), "a=1");   // unrelated island
        var b = _graph.Upsert(NewId(), "a+2=");
        _graph.Recompute();
        var aResultBefore = a.Result;
        var bResultBefore = b.Result;
        _evaluator.Reset();

        var x = _graph.Upsert(xId, "x=7");
        var changed = _graph.Recompute();

        // Exactly x, y, q re-evaluated — the unrelated island never reached the evaluator.
        Assert.Equal(3, _evaluator.Calls);
        Assert.Equal("7", x.Result!.DisplayText);
        Assert.Equal("9", y.Result!.DisplayText);
        Assert.Equal("10", q.Result!.DisplayText);
        Assert.Equal(new[] { x.Id, y.Id, q.Id }.ToHashSet(), changed.Select(n => n.Id).ToHashSet());
        Assert.Same(aResultBefore, a.Result);
        Assert.Same(bResultBefore, b.Result);
    }

    [Fact]
    public void RenamingADefinitionsSymbol_ReEvaluatesItsOrphanedDependents()
    {
        // Renaming "x=5" to "y=5" severs the edge to x's users BEFORE recompute rebuilds edges, so
        // the ripple can't reach them through the graph — Upsert must seed the pre-edit dependents
        // dirty itself, or the query keeps a stale substituted 6.
        var defId = NewId();
        _graph.Upsert(defId, "x=5");
        var q = _graph.Upsert(NewId(), "x+1=");
        _graph.Recompute();
        Assert.Equal("6", q.Result!.DisplayText);

        _graph.Upsert(defId, "y=5");
        var changed = _graph.Recompute();

        Assert.Contains(q, changed);
        Assert.Equal(EvaluationKind.Symbolic, q.Result!.Kind);
        Assert.Contains("x", q.Result.DisplayText);   // x is unbound again → honestly symbolic
    }

    [Fact]
    public void UnchangedLatexUpsertTriggersNoReEvaluation()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        _graph.Upsert(NewId(), "x+1=");
        _graph.Recompute();
        _evaluator.Reset();

        _graph.Upsert(xId, "x=5"); // same LaTeX — nothing changed
        var changed = _graph.Recompute();

        Assert.Equal(0, _evaluator.Calls);
        Assert.Empty(changed);
    }

    [Fact]
    public void RecomputeReturnsOnlyNodesWhoseResultChanged()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var q = _graph.Upsert(NewId(), "0x+1=");   // depends on x, but its value never varies
        _graph.Recompute();
        _evaluator.Reset();

        _graph.Upsert(xId, "x=7");
        var changed = _graph.Recompute();

        // Both re-evaluated (q depends on x) but only x's RESULT differs.
        Assert.Equal(2, _evaluator.Calls);
        Assert.Equal("1", q.Result!.DisplayText);
        Assert.Equal(new[] { xId }, changed.Select(n => n.Id));
    }

    [Fact]
    public void RemovingADefinitionLeavesDependentsSymbolic()
    {
        var xId = NewId();
        _graph.Upsert(xId, "x=5");
        var q = _graph.Upsert(NewId(), "x+1=");
        _graph.Recompute();
        Assert.Equal("6", q.Result!.DisplayText);

        Assert.True(_graph.Remove(xId));
        _graph.Recompute();

        // With no definer left, x is honestly free again — not silently stuck at 5.
        Assert.Equal(EvaluationKind.Symbolic, q.Result!.Kind);
        Assert.Contains("x", q.Result.DisplayText);
        Assert.Null(_graph.Find(xId));
        Assert.False(_graph.Remove(xId));
    }

    // ---- cycles (kickoff decision 4: all Error, no partial eval) ------------------------------

    [Fact]
    public void CycleMembersAllErrorAndNothingPartiallyEvaluates()
    {
        var a = _graph.Upsert(NewId(), "a=b+1");
        var b = _graph.Upsert(NewId(), "b=a+1");
        _evaluator.Reset();

        var changed = _graph.Recompute();

        Assert.All(new[] { a, b }, n =>
        {
            Assert.Equal(EvaluationKind.Error, n.Result!.Kind);
            Assert.False(n.Result.IsComputed);
        });
        Assert.Equal(0, _evaluator.Calls); // no partial evaluation of cycle members
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void NodesOutsideTheCycleStillEvaluate()
    {
        _graph.Upsert(NewId(), "a=b+1");
        _graph.Upsert(NewId(), "b=a+1");
        var c = _graph.Upsert(NewId(), "c=2");
        var q = _graph.Upsert(NewId(), "c+1=");

        _graph.Recompute();

        Assert.Equal("2", c.Result!.DisplayText);
        Assert.Equal("3", q.Result!.DisplayText);
    }

    [Fact]
    public void DownstreamOfACycleStaysSymbolicRatherThanWrong()
    {
        _graph.Upsert(NewId(), "a=b+1");
        _graph.Upsert(NewId(), "b=a+1");
        var q = _graph.Upsert(NewId(), "a+1=");

        _graph.Recompute();

        // The query itself is not in the cycle; it evaluates, but with no usable value for "a".
        Assert.Equal(EvaluationKind.Symbolic, q.Result!.Kind);
        Assert.Contains("a", q.Result.DisplayText);
    }

    [Fact]
    public void BreakingACycleRecovers()
    {
        var aId = NewId();
        _graph.Upsert(aId, "a=b+1");
        var b = _graph.Upsert(NewId(), "b=a+1");
        _graph.Recompute();

        var a = _graph.Upsert(aId, "a=1");
        _graph.Recompute();

        Assert.Equal("1", a.Result!.DisplayText);
        Assert.Equal("2", b.Result!.DisplayText);
        Assert.Equal(EvaluationKind.Number, b.Result.Kind);
    }

    [Fact]
    public void SelfReferenceIsACycleOfOne()
    {
        var a = _graph.Upsert(NewId(), "a=a+1");

        _graph.Recompute();

        Assert.Equal(EvaluationKind.Error, a.Result!.Kind);
    }

    // ---- duplicate definitions (kickoff decision 4: topmost wins, rest Conflict) --------------

    [Fact]
    public void DuplicateDefinitionTopmostRegionWins()
    {
        // Inserted bottom-first to prove the y-coordinate, not insertion order, decides.
        var lower = _graph.Upsert(NewId(), "x=9", region: new InkBounds(0, 200, 50, 20));
        var upper = _graph.Upsert(NewId(), "x=5", region: new InkBounds(0, 10, 50, 20));
        var q = _graph.Upsert(NewId(), "x+1=");

        _graph.Recompute();

        Assert.False(upper.IsConflict);
        Assert.True(lower.IsConflict);
        Assert.Equal(EvaluationKind.Error, lower.Result!.Kind);
        Assert.Equal("6", q.Result!.DisplayText); // dependents bind to the winner
    }

    [Fact]
    public void MovingDefinitionAcrossPeer_SwitchesWinnerAndRecomputesDependents()
    {
        var firstId = NewId();
        var secondId = NewId();
        var first = _graph.Upsert(firstId, "x=5", region: new InkBounds(0, 10, 50, 20));
        var second = _graph.Upsert(secondId, "x=9", region: new InkBounds(0, 200, 50, 20));
        var q = _graph.Upsert(NewId(), "x+1=");
        _graph.Recompute();
        Assert.Equal("6", q.Result!.DisplayText);
        _evaluator.Reset();

        // Same id and LaTeX, but it moved above the old winner: region Y is part of graph semantics.
        _graph.Upsert(secondId, "x=9", region: new InkBounds(0, 0, 50, 20));
        var changed = _graph.Recompute();

        Assert.True(first.IsConflict);
        Assert.False(second.IsConflict);
        Assert.Equal("10", q.Result!.DisplayText);
        Assert.Equal(2, _evaluator.Calls); // promoted winner + query; conflict loser never reaches the CAS
        Assert.Equal(new[] { first.Id, second.Id, q.Id }.ToHashSet(), changed.Select(n => n.Id).ToHashSet());
    }

    [Fact]
    public void DuplicateDefinitionWithoutRegionsFirstInsertedWins()
    {
        var first = _graph.Upsert(NewId(), "x=5");
        var second = _graph.Upsert(NewId(), "x=9");
        var q = _graph.Upsert(NewId(), "x+1=");

        _graph.Recompute();

        Assert.False(first.IsConflict);
        Assert.True(second.IsConflict);
        Assert.Equal("6", q.Result!.DisplayText);
    }

    [Fact]
    public void RemovingTheWinnerPromotesTheConflictedDefinition()
    {
        var winnerId = NewId();
        _graph.Upsert(winnerId, "x=5");
        var loser = _graph.Upsert(NewId(), "x=9");
        var q = _graph.Upsert(NewId(), "x+1=");
        _graph.Recompute();
        Assert.True(loser.IsConflict);

        _graph.Remove(winnerId);
        _graph.Recompute();

        Assert.False(loser.IsConflict);
        Assert.Equal("9", loser.Result!.DisplayText);
        Assert.Equal("10", q.Result!.DisplayText);
    }

    [Fact]
    public void EditingAConflictAwayRestoresBothNodes()
    {
        _graph.Upsert(NewId(), "x=5");
        var otherId = NewId();
        var other = _graph.Upsert(otherId, "x=9");
        _graph.Recompute();
        Assert.True(other.IsConflict);

        _graph.Upsert(otherId, "z=9"); // re-bound to a fresh symbol
        _graph.Recompute();

        Assert.False(other.IsConflict);
        Assert.Equal("9", other.Result!.DisplayText);
    }

    // ---- robustness ----------------------------------------------------------------------------

    [Fact]
    public void MalformedLatexBecomesAnErrorResultNotACrash()
    {
        var bad = _graph.Upsert(NewId(), "garbage^^^");
        var ok = _graph.Upsert(NewId(), "2+2=");

        _graph.Recompute();

        Assert.Equal(EvaluationKind.Error, bad.Result!.Kind);
        Assert.Equal("4", ok.Result!.DisplayText); // one bad cell never poisons the sheet
    }

    [Fact]
    public void SeamOneLinkageRidesTheNode()
    {
        var tokens = new[]
        {
            new RecognizedToken("x", new[] { Guid.NewGuid() }, new InkBounds(0, 0, 10, 12), 0.98),
        };
        var region = new InkBounds(0, 0, 60, 20);

        var node = _graph.Upsert(NewId(), "x=5", tokens, region);

        Assert.Same(tokens, node.Tokens);
        Assert.Equal(region, node.Region);
    }
}
