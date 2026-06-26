namespace Penumbra.Cas;

/// <summary>The result of evaluating a mathematical expression.</summary>
public sealed record EvaluationResult(string Latex, string DisplayText, bool IsComputed);
