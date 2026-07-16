using Penumbra.Cas;
using Penumbra.Core;

namespace Penumbra.Sheet.Tests;

/// <summary>
/// Slice 6 regression guard: the new evaluator solve-target refusal (a multi-unknown equation refuses
/// instead of silently solving for the first unknown) must NOT touch definitions. A bare-variable
/// definition is routed through the analyzer as a definition and evaluated as its right-hand side only,
/// so <c>y=2x+1</c> or <c>y=ax+b</c> never reaches the multi-unknown solve path. These prove the
/// mandatory reactive-dependency page keeps working end-to-end through the real CAS.
/// </summary>
public sealed class SheetAlgebraDefinitionTests
{
    private readonly SheetGraph _graph =
        new(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer());

    private static Guid NewId() => Guid.NewGuid();

    [Fact]
    public void BareVariableDefinitionWithFreeRhsStaysSymbolic()
    {
        // y=2x+1 is a DEFINITION (bare-variable LHS), not a two-unknown equation to solve. With x
        // undefined it evaluates its RHS to a symbolic value — it must not refuse as "multiple unknowns".
        var y = _graph.Upsert(NewId(), "y=2x+1");

        _graph.Recompute();

        Assert.Equal(NodeRole.Definition, y.Role);
        Assert.Equal("y", y.DefinedSymbol);
        Assert.True(y.Result!.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, y.Result.Kind);
        Assert.Contains("x", y.Result.DisplayText);
    }

    [Fact]
    public void QuadraticDefinitionStaysSymbolic()
    {
        // Mandatory positive fixture y=x^2: a definition, evaluated as its RHS, not a solve.
        var y = _graph.Upsert(NewId(), "y=x^2");

        _graph.Recompute();

        Assert.Equal(NodeRole.Definition, y.Role);
        Assert.True(y.Result!.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, y.Result.Kind);
        Assert.Contains("x", y.Result.DisplayText);
    }

    [Fact]
    public void MultiLineReactiveDependencyPageStillResolves()
    {
        // The mandatory a=2, b=1, y=ax+b page: a and b are numeric definitions; y binds them and stays
        // symbolic in x (2x+1). This is the reactive dependency flow the solve-target policy must preserve.
        var a = _graph.Upsert(NewId(), "a=2");
        var b = _graph.Upsert(NewId(), "b=1");
        var y = _graph.Upsert(NewId(), "y=ax+b");

        _graph.Recompute();

        Assert.Equal("2", a.Result!.DisplayText);
        Assert.Equal("1", b.Result!.DisplayText);

        Assert.Equal(NodeRole.Definition, y.Role);
        Assert.True(y.Result!.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, y.Result.Kind);
        Assert.Contains("x", y.Result.DisplayText);         // a,b resolved; x remains the free variable
        Assert.DoesNotContain("a", y.Result.DisplayText);   // a was bound to 2, not left free
        Assert.DoesNotContain("b", y.Result.DisplayText);   // b was bound to 1, not left free
    }

    [Fact]
    public void ReactiveChainUpdatesWhenADefinitionChanges()
    {
        // Change a from 2 to 5: y must recompute to 5x+1, proving the dependency ripple is intact.
        var aId = NewId();
        _graph.Upsert(aId, "a=2");
        _graph.Upsert(NewId(), "b=1");
        var y = _graph.Upsert(NewId(), "y=ax+b");
        _graph.Recompute();

        _graph.Upsert(aId, "a=5");
        _graph.Recompute();

        Assert.True(y.Result!.IsComputed);
        Assert.Equal(EvaluationKind.Symbolic, y.Result.Kind);
        Assert.Contains("5", y.Result.DisplayText);
        Assert.Contains("x", y.Result.DisplayText);
    }

    [Fact]
    public void MultiUnknownEquationStatementRefusesInTheSheet()
    {
        // A genuine two-unknown equation written as a statement (not a definition) refuses through the
        // Sheet too — no silently-solved-for-x answer reaches a node result.
        var eq = _graph.Upsert(NewId(), "2x+3y=6");

        _graph.Recompute();

        Assert.Equal(NodeRole.Statement, eq.Role);
        Assert.False(eq.Result!.IsComputed);
        Assert.Equal(EvaluationKind.Error, eq.Result.Kind);
    }
}
