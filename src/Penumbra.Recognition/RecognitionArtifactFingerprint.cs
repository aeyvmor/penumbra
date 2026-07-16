using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Penumbra.Recognition;

/// <summary>Computes the identity of the model weights and interpretation metadata shipped together.</summary>
public static class RecognitionArtifactFingerprint
{
    public const string ModelFileName = "crohme_geo_cnn.onnx";
    public const string MetadataFileName = "crohme_geo_cnn.meta.json";

    /// <summary>
    /// Returns a lowercase SHA-256 over length-delimited file names and bytes. Both files participate so
    /// a calibration/class/preprocessing metadata change cannot masquerade as the same runtime model.
    /// </summary>
    public static string Compute(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string fullDirectory = Path.GetFullPath(modelDirectory);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, fullDirectory, ModelFileName);
        AppendFile(hash, fullDirectory, MetadataFileName);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFile(IncrementalHash hash, string directory, string fileName)
    {
        byte[] name = Encoding.UTF8.GetBytes(fileName);
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(length, name.Length);
        hash.AppendData(length);
        hash.AppendData(name);

        string path = Path.Combine(directory, fileName);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Recognition artifact is missing.", path);
        }
        BinaryPrimitives.WriteInt64LittleEndian(length, info.Length);
        hash.AppendData(length);
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer.AsSpan(0, read));
        }
    }
}
