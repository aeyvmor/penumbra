using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>Recognizes online strokes as mathematical tokens.</summary>
public interface IRecognizer
{
    /// <summary>Recognizes the supplied stroke sequence.</summary>
    RecognitionResult Recognize(IReadOnlyList<Stroke> strokes);

    /// <summary>
    /// Recognizes off the calling thread (Phase 4.5a — live recognition must never block the UI).
    /// Callers must hand in a stable snapshot of the strokes, not a live collection. The default
    /// bridges to <see cref="Recognize"/> on the thread pool; engines with cancellable stages
    /// override to honour <paramref name="cancellationToken"/> mid-pipeline.
    /// </summary>
    Task<RecognitionResult> RecognizeAsync(
        IReadOnlyList<Stroke> strokes, CancellationToken cancellationToken = default)
        => Task.Run(() => Recognize(strokes), cancellationToken);
}
