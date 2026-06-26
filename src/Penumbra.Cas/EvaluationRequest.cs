namespace Penumbra.Cas;

/// <summary>A LaTeX expression plus the variable bindings available to the evaluator.</summary>
public sealed record EvaluationRequest(
    string Latex,
    IReadOnlyDictionary<string, string> Variables);
