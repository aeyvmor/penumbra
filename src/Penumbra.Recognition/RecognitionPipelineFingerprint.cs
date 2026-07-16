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
    /// sequencing/products/relations/functions/brackets under <see cref="SpatialLayoutParser"/>. Version 3
    /// separates close-written fraction bars before symbol classification and resolves the model's
    /// <c>x</c>/<c>\times</c> probability pair from expression context, then tightens fused-radical geometry
    /// so a preceding variable or equals sign cannot consume the one structural split attempt. These changes
    /// can alter an accepted token, its confidence, or the serialized LaTeX, so an older cached read must be
    /// reclassified;
    /// <c>Penumbra.Runtime.PageRecognitionCache</c> reads this to invalidate stale hints.
    /// </summary>
    public const string Current = "r1-recursive-v3";
}
