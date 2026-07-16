using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

public sealed class RecognitionPipelineFingerprintTests
{
    [Fact]
    public void CurrentFingerprintPinsTheRecognitionRepairContract()
    {
        // Close fraction grouping, contextual x/times resolution, and fused-radical disambiguation can
        // change the accepted read. An older hint must reclassify instead of bypassing the repaired pipeline.
        Assert.Equal("r1-recursive-v3", RecognitionPipelineFingerprint.Current);
    }
}
