namespace Penumbra.Cas;

/// <summary>
/// The structural facts the dependency graph needs about one expression, without the graph ever
/// touching a CAS. Produced by <see cref="IExpressionAnalyzer"/>.
/// </summary>
/// <param name="DefinedSymbol">
/// The single LHS variable when the expression is a definition (<c>x = 5</c>, <c>y = x + 2</c>);
/// <c>null</c> for queries, equations, and statements that don't bind a symbol.
/// </param>
/// <param name="FreeVariables">
/// The variables this expression depends on (its inbound graph edges). For a definition this is the
/// RHS variables — a self-reference like <c>a = a+1</c> keeps <c>a</c> here so the graph can report
/// the cycle. For a query/statement it is every variable that appears. Empty when there are none.
/// </param>
/// <param name="IsQuery">
/// True for a trailing-<c>=</c> compute request such as <c>2 + x =</c> (asks for a value) as opposed
/// to a full equation <c>2x + 3 = 7</c> (asks to be solved) or a bare statement.
/// </param>
/// <param name="SolvedSymbol">
/// The sole unknown of a full equation such as <c>2x = 4</c>. This advertises a possible derived value
/// to the sheet; it is not a definition and becomes bindable only when evaluation returns one verified
/// solution. Null for definitions, queries, constants, and equations with zero or multiple unknowns.
/// </param>
public sealed record ExpressionAnalysis(
    string? DefinedSymbol,
    IReadOnlySet<string> FreeVariables,
    bool IsQuery,
    string? SolvedSymbol = null);
