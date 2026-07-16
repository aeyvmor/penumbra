namespace Penumbra.Recognition;

/// <summary>
/// Identifies the complete recognition interpretation persisted as a schema-v4 cache hint.
/// </summary>
/// <remarks>
/// Change <see cref="Current"/> whenever segmentation, classification, layout grammar, or token
/// assembly changes in a way that can alter a cached read. The fingerprint is deliberately owned by
/// Recognition rather than Core: Core persists an opaque string and never decides whether a cache is
/// current.
/// </remarks>
public static class RecognitionPipelineFingerprint
{
    /// <summary>
    /// The current R1 recognition contract: recursive scripts, fractions, and radicals plus linear
    /// sequencing/products/relations/functions/brackets under <see cref="SpatialLayoutParser"/>. Bumped from
    /// <c>r1-spatial-v1</c> because Slice 5 changes both structural acceptance and serialized LaTeX. The
    /// grammar now REFUSES lines the old flat assembler silently accepted (unmatched brackets, raised
    /// tokens, ambiguous function words, …) and emits tree-serialized LaTeX for accepted lines — a v1-v3
    /// (and any prior v4) cached read cannot be trusted to reflect this contract, so it must not survive
    /// the migration; <c>Penumbra.Runtime.PageRecognitionCache</c> reads this to invalidate stale hints.
    /// </summary>
    public const string Current = "r1-recursive-v1";
}
