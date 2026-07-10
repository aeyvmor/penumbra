using Penumbra.Cas;

namespace Penumbra.Sheet.Tests;

/// <summary>
/// The CAS-agnostic proof (kickoff decision 2): the graph's dirty-marking, edge-binding, topological
/// pull, cycle, and conflict semantics all run against a toy analyzer/evaluator with no AngouriMath in
/// sight — so a future units/regression evaluator plugs in without touching the graph.
/// </summary>
public sealed class SheetGraphWithFakesTests
{
    private readonly CountingEvaluator _evaluator = new(new FakeEvaluator());
    private readonly SheetGraph _graph;

    public SheetGraphWithFakesTests()
    {
        _graph = new SheetGraph(_evaluator, new FakeAnalyzer());
    }

    [Fact]
    public void DefinitionValueFlowsDownstreamThroughBindings()
    {
        _graph.Upsert(Guid.NewGuid(), "x=5");
        var q = _graph.Upsert(Guid.NewGuid(), "x+1=");

        _graph.Recompute();

        // The fake evaluator splices bindings as [value]: proof the definition's result reached the query.
        Assert.Equal("[5]+1=", q.Result!.DisplayText);
    }

    [Fact]
    public void EditReEvaluatesOnlyDependentsUnderTheFakeToo()
    {
        var xId = Guid.NewGuid();
        _graph.Upsert(xId, "x=5");
        var q = _graph.Upsert(Guid.NewGuid(), "x+1=");
        _graph.Upsert(Guid.NewGuid(), "k=3"); // unrelated
        _graph.Recompute();
        _evaluator.Reset();

        _graph.Upsert(xId, "x=8");
        _graph.Recompute();

        Assert.Equal(2, _evaluator.Calls); // x and q, never k
        Assert.Equal("[8]+1=", q.Result!.DisplayText);
    }

    [Fact]
    public void CycleSemanticsHoldWithoutACas()
    {
        var a = _graph.Upsert(Guid.NewGuid(), "a=b");
        var b = _graph.Upsert(Guid.NewGuid(), "b=a");

        _graph.Recompute();

        Assert.All(new[] { a, b }, n => Assert.Equal(EvaluationKind.Error, n.Result!.Kind));
    }

    [Fact]
    public void ConflictSemanticsHoldWithoutACas()
    {
        var first = _graph.Upsert(Guid.NewGuid(), "x=1");
        var second = _graph.Upsert(Guid.NewGuid(), "x=2");

        _graph.Recompute();

        Assert.False(first.IsConflict);
        Assert.True(second.IsConflict);
        Assert.Equal(EvaluationKind.Error, second.Result!.Kind);
    }
}
