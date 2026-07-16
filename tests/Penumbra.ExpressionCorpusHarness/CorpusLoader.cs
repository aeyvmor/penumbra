using System.Security.Cryptography;

namespace Penumbra.ExpressionCorpus;

public sealed class CorpusLoadException : Exception
{
    public CorpusLoadException(CorpusErrorCode code)
        : base($"Corpus loading failed ({code}).")
    {
        Code = code;
    }

    public CorpusLoadException(CorpusErrorCode code, Exception innerException)
        : base($"Corpus loading failed ({code}).", innerException)
    {
        Code = code;
    }

    public CorpusErrorCode Code { get; }
}

public static class ExpressionCorpusLoader
{
    public static ExpressionCorpusSuite Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        try
        {
            string canonicalRoot = Path.GetFullPath(root);
            if (!Directory.Exists(canonicalRoot))
            {
                throw new CorpusLoadException(CorpusErrorCode.ManifestMismatch);
            }
            RejectReparsePoint(canonicalRoot);

            string manifestPath = Path.Combine(canonicalRoot, "manifest.v1.json");
            RejectReparsePath(canonicalRoot, manifestPath);
            byte[] manifestBytes = ReadBoundedFile(
                manifestPath,
                CorpusResourceLimitsV1.MaximumManifestBytes);
            CorpusManifestV1 manifest = CorpusJson.DeserializeManifest(manifestBytes);
            string manifestSha256 = Hash(CorpusJson.SerializeToUtf8Bytes(manifest));

            if (manifest.Entries is null || manifest.Entries.Any(entry => entry is null))
            {
                throw new CorpusLoadException(CorpusErrorCode.MissingValue);
            }
            if (manifest.Entries.Count > CorpusResourceLimitsV1.MaximumCases)
            {
                throw new CorpusLoadException(CorpusErrorCode.ResourceLimitExceeded);
            }

            var cases = new List<ExpressionCaseV1>(manifest.Entries.Count);
            var listedPaths = new HashSet<string>(StringComparer.Ordinal);
            long totalBytes = manifestBytes.Length;
            foreach (CorpusManifestEntryV1 entry in manifest.Entries)
            {
                string canonicalRelativePath = ValidateAndNormalizeRelativePath(entry.RelativePath, entry.Partition);
                if (!listedPaths.Add(canonicalRelativePath))
                {
                    throw new CorpusLoadException(CorpusErrorCode.ManifestMismatch);
                }

                string casePath = ResolveUnderRoot(canonicalRoot, canonicalRelativePath);
                RejectReparsePath(canonicalRoot, casePath);
                byte[] caseBytes = ReadBoundedFile(
                    casePath,
                    CorpusResourceLimitsV1.MaximumCaseBytes);
                totalBytes += caseBytes.Length;
                if (totalBytes > CorpusResourceLimitsV1.MaximumCorpusBytes)
                {
                    throw new CorpusLoadException(CorpusErrorCode.ResourceLimitExceeded);
                }
                if (!string.Equals(Hash(caseBytes), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CorpusLoadException(CorpusErrorCode.InvalidContentHash);
                }

                cases.Add(CorpusJson.DeserializeCase(caseBytes));
            }

            HashSet<string> actualPaths = EnumerateCasePaths(canonicalRoot);
            if (!listedPaths.SetEquals(actualPaths))
            {
                throw new CorpusLoadException(CorpusErrorCode.ManifestMismatch);
            }

            return new ExpressionCorpusSuite(manifest, cases, manifestSha256);
        }
        catch (CorpusLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Text.Json.JsonException
            or CryptographicException)
        {
            throw new CorpusLoadException(CorpusErrorCode.ManifestMismatch, exception);
        }
    }

    private static HashSet<string> EnumerateCasePaths(string root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string partitionDirectory in new[] { "development", "held-out" })
        {
            string directory = Path.Combine(root, partitionDirectory);
            if (!Directory.Exists(directory))
            {
                continue;
            }
            RejectReparsePoint(directory);
            if (Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new CorpusLoadException(CorpusErrorCode.InvalidManifestPath);
            }
            foreach (string file in Directory.EnumerateFiles(directory, "*.case.json", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(file);
                if (!CorpusPathRulesV1.IsCaseFileName(Path.GetFileName(file)))
                {
                    throw new CorpusLoadException(CorpusErrorCode.InvalidManifestPath);
                }
                if (paths.Count >= CorpusResourceLimitsV1.MaximumCases)
                {
                    throw new CorpusLoadException(CorpusErrorCode.ResourceLimitExceeded);
                }
                paths.Add($"{partitionDirectory}/{Path.GetFileName(file)}");
            }
        }
        return paths;
    }

    private static string ValidateAndNormalizeRelativePath(string relativePath, CorpusPartitionV1 partition)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Contains('\\')
            || Path.IsPathRooted(relativePath))
        {
            throw new CorpusLoadException(CorpusErrorCode.InvalidManifestPath);
        }

        string[] parts = relativePath.Split('/', StringSplitOptions.None);
        string expectedDirectory = partition == CorpusPartitionV1.Development ? "development" : "held-out";
        if (parts.Length != 2
            || !string.Equals(parts[0], expectedDirectory, StringComparison.Ordinal)
            || parts.Any(part => string.IsNullOrEmpty(part) || part is "." or "..")
            || !CorpusPathRulesV1.IsCaseFileName(parts[1]))
        {
            throw new CorpusLoadException(CorpusErrorCode.InvalidManifestPath);
        }
        return string.Join('/', parts);
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        string resolved = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, PathComparison))
        {
            throw new CorpusLoadException(CorpusErrorCode.InvalidManifestPath);
        }
        return resolved;
    }

    private static void RejectReparsePath(string root, string target)
    {
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        foreach (string segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new CorpusLoadException(CorpusErrorCode.InvalidManifestPath);
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new CorpusLoadException(CorpusErrorCode.ResourceLimitExceeded);
        }

        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                return output.ToArray();
            }
            if (output.Length + read > maximumBytes)
            {
                throw new CorpusLoadException(CorpusErrorCode.ResourceLimitExceeded);
            }
            output.Write(buffer, 0, read);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
