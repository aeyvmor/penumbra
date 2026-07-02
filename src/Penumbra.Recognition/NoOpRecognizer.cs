using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>Phase 0 recognizer placeholder used until the ONNX path lands.</summary>
public sealed class NoOpRecognizer : IRecognizer
{
    /// <inheritdoc />
    public RecognitionResult Recognize(IReadOnlyList<Stroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        return new RecognitionResult(
            Latex: string.Empty,
            Tokens: Array.Empty<RecognizedToken>(),
            Confidence: 0,
            MinConfidence: 0);
    }
}
