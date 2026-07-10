namespace Penumbra.Cas;

/// <summary>
/// Classifies an expression's role in the dependency graph (definition / query / statement) and the
/// symbols it defines and depends on — the second CAS question the graph asks, alongside
/// <see cref="IEvaluator"/>. Kept behind this seam so the graph never parses math itself.
/// </summary>
public interface IExpressionAnalyzer
{
    /// <summary>Analyzes one LaTeX expression. Must never throw — malformed input returns a
    /// no-symbol, empty-dependency, non-query result.</summary>
    ExpressionAnalysis Analyze(string latex);
}
