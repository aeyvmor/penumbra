using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>The LaTeX and stroke-token alignment returned by recognition.</summary>
public sealed record RecognitionResult(
    string Latex,
    IReadOnlyList<RecognizedToken> Tokens,
    double Confidence);
