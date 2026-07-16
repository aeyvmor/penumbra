using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

public sealed class RecognitionArtifactFingerprintTests
{
    [Fact]
    public void Compute_IdentifiesWeightsAndMetadataTogetherDeterministically()
    {
        string directory = CreateDirectory();
        try
        {
            WriteArtifacts(directory, [1, 2, 3], "metadata-a");
            string initial = RecognitionArtifactFingerprint.Compute(directory);
            string repeated = RecognitionArtifactFingerprint.Compute(directory);

            WriteArtifacts(directory, [1, 2, 4], "metadata-a");
            string changedWeights = RecognitionArtifactFingerprint.Compute(directory);
            WriteArtifacts(directory, [1, 2, 3], "metadata-b");
            string changedMetadata = RecognitionArtifactFingerprint.Compute(directory);

            Assert.Equal(initial, repeated);
            Assert.Matches("^[0-9a-f]{64}$", initial);
            Assert.NotEqual(initial, changedWeights);
            Assert.NotEqual(initial, changedMetadata);
            Assert.NotEqual(changedWeights, changedMetadata);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Compute_RequiresBothArtifactFiles()
    {
        string directory = CreateDirectory();
        try
        {
            File.WriteAllBytes(
                Path.Combine(directory, RecognitionArtifactFingerprint.ModelFileName),
                [1, 2, 3]);

            Assert.Throws<FileNotFoundException>(() =>
                RecognitionArtifactFingerprint.Compute(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "penumbra-recognition-fingerprint-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteArtifacts(string directory, byte[] model, string metadata)
    {
        File.WriteAllBytes(
            Path.Combine(directory, RecognitionArtifactFingerprint.ModelFileName),
            model);
        File.WriteAllText(
            Path.Combine(directory, RecognitionArtifactFingerprint.MetadataFileName),
            metadata);
    }
}
