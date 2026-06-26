namespace Penumbra.Cas;

/// <summary>Evaluates recognized math expressions behind a pluggable engine seam.</summary>
public interface IEvaluator
{
    /// <summary>Evaluates one expression request.</summary>
    EvaluationResult Evaluate(EvaluationRequest request);
}
