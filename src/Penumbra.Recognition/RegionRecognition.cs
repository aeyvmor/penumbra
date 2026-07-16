namespace Penumbra.Recognition;

/// <summary>
/// One line-region paired with its recognition (Phase 5a incremental read). <see cref="Dirty"/> is true
/// when the region was (re-)recognized this pass — its stroke set changed since it was last read; false
/// when <see cref="Result"/> was reused unchanged from the previous pass. Callers feed the previous
/// pass's list back in to get this reuse. Persisted geometry may set
/// <see cref="RequiresAuthoritativeRecognition"/> so region identity is reusable while its cached labels are not.
/// </summary>
public sealed record RegionRecognition(
    InkRegion Region,
    RecognitionResult Result,
    bool Dirty,
    bool RequiresAuthoritativeRecognition = false);
