using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

public sealed class RecognitionPipelineFingerprintTests
{
    [Fact]
    public void CurrentFingerprintPinsTheRecursivePipelineContract()
    {
        // Slice 5 changes refusal/acceptance and serialized LaTeX for scripts, fractions, and radicals.
        // A Slice-4 spatial-v1 hint must therefore reclassify rather than bypass recursive ownership.
        Assert.Equal("r1-recursive-v1", RecognitionPipelineFingerprint.Current);
    }
}
