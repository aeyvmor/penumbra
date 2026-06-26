using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>Recognizes online strokes as mathematical tokens.</summary>
public interface IRecognizer
{
    /// <summary>Recognizes the supplied stroke sequence.</summary>
    RecognitionResult Recognize(IReadOnlyList<Stroke> strokes);
}
