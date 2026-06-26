namespace Penumbra.Graphing;

/// <summary>A detected graphable expression such as y = f(x).</summary>
public sealed record GraphCandidate(string VariableName, string ExpressionLatex);
