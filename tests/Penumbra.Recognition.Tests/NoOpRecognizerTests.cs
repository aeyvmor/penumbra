using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

public sealed class NoOpRecognizerTests
{
    [Fact]
    public void RecognizeReturnsEmptyResult()
    {
        var recognizer = new NoOpRecognizer();

        var result = recognizer.Recognize(Array.Empty<Stroke>());

        Assert.Empty(result.Latex);
        Assert.Empty(result.Tokens);
        Assert.Equal(0, result.Confidence);
    }
}
