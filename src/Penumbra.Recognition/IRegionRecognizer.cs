using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Recognizes a stable snapshot of page ink as independent line regions while preserving region
/// identity and prior recognition results across incremental passes.
/// </summary>
/// <remarks>
/// The caller owns the round trip: only the list returned by the last successfully applied pass may
/// be supplied as <c>previous</c> on the next pass. A cancelled or superseded pass must
/// be discarded wholesale. Implementations reuse a prior <see cref="RecognitionResult"/> verbatim
/// when its matched region has the same stroke set; only dirty regions may invoke classification.
/// </remarks>
public interface IRegionRecognizer
{
    /// <summary>
    /// Segments and incrementally recognizes <paramref name="strokes"/>, reusing clean results from
    /// <paramref name="previous"/>. The returned list is the complete next round-trip state.
    /// </summary>
    IReadOnlyList<RegionRecognition> RecognizeRegions(
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<RegionRecognition>? previous = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <see cref="RecognizeRegions"/> off the calling thread. Callers must pass stable snapshots
    /// of both collections and treat cancellation as an atomic failure: no partial list is returned.
    /// </summary>
    Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<RegionRecognition>? previous = null,
        CancellationToken cancellationToken = default);
}
