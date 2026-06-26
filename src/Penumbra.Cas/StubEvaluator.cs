namespace Penumbra.Cas;

/// <summary>Phase 0 evaluator placeholder used until the AngouriMath wrapper lands.</summary>
public sealed class StubEvaluator : IEvaluator
{
    /// <inheritdoc />
    public EvaluationResult Evaluate(EvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new EvaluationResult(
            request.Latex,
            "CAS engine scaffolded; AngouriMath integration is next.",
            IsComputed: false);
    }
}
