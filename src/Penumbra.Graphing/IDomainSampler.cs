namespace Penumbra.Graphing;

/// <summary>
/// Samples a <see cref="GraphCandidate"/>'s right-hand side over a domain into a gap-honest series. Compiles
/// the expression once per call and evaluates the compiled numeric lambda per sample — never recompiles
/// per-point. Deterministic: the same candidate, domain, and sample count always produce bit-identical output.
/// </summary>
public interface IDomainSampler
{
    /// <summary>
    /// Samples <paramref name="candidate"/> at <paramref name="sampleCount"/> evenly spaced points across
    /// <paramref name="domain"/> (inclusive of both endpoints). <paramref name="sampleCount"/> must be within
    /// <see cref="DomainSampler.MinSampleCount"/>..<see cref="DomainSampler.MaxSampleCount"/>, else the call
    /// throws — an out-of-range count is a caller contract violation, not a business refusal.
    /// </summary>
    GraphSamplingOutcome SampleSeries(GraphCandidate candidate, GraphDomain domain, int sampleCount);
}
