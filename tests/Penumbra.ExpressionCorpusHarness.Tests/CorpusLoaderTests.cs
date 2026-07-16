using System.Security.Cryptography;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class CorpusLoaderTests
{
    [Fact]
    public async Task Cli_SanitizesLoadFailuresWithoutLeakingPrivatePaths()
    {
        string privatePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "private-path-canary",
            Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();

        int exitCode = await CorpusCli.RunAsync(
            ["validate", "--root", privatePath],
            output);
        string text = output.ToString();

        Assert.Equal(2, exitCode);
        Assert.Contains("error=manifest_mismatch", text, StringComparison.Ordinal);
        Assert.Contains("overall=FAIL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path-canary", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RequiresManifestToExactlyCoverCaseFilesAndHashes()
    {
        using var directory = new TemporaryCorpusDirectory();
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        directory.WriteCase(@case, "development/0001.case.json");
        directory.WriteManifest(@case, "development/0001.case.json");

        ExpressionCorpusSuite suite = ExpressionCorpusLoader.Load(directory.Path);

        Assert.Single(suite.Cases);
        Assert.Empty(CorpusValidator.ValidateSuite(suite));

        directory.WriteCase(
            CorpusTestData.ValidCase("dev-unlisted-001", "session-unlisted-001"),
            "development/unlisted.case.json");
        CorpusLoadException exception = Assert.Throws<CorpusLoadException>(
            () => ExpressionCorpusLoader.Load(directory.Path));
        Assert.Equal(CorpusErrorCode.ManifestMismatch, exception.Code);
    }

    [Fact]
    public void Loader_RejectsHashMismatchBeforeDeserializingCase()
    {
        using var directory = new TemporaryCorpusDirectory();
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        directory.WriteCase(@case, "development/0001.case.json");
        directory.WriteManifest(@case, "development/0001.case.json", sha256: new string('0', 64));

        CorpusLoadException exception = Assert.Throws<CorpusLoadException>(
            () => ExpressionCorpusLoader.Load(directory.Path));

        Assert.Equal(CorpusErrorCode.InvalidContentHash, exception.Code);
    }

    [Fact]
    public void PublicSyntheticFixture_IsStrictAndSemanticallyValid()
    {
        string fixturePath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Synthetic",
            "v1",
            "linear-two-token.case.json");

        ExpressionCaseV1 fixture = CorpusJson.DeserializeCase(File.ReadAllText(fixturePath));

        Assert.Equal(CorpusDataClassificationV1.PublicSynthetic, fixture.Capture.DataClassification);
        Assert.Equal(CorpusCaptureSourceV1.Synthetic, fixture.Capture.Source);
        Assert.Empty(CorpusValidator.ValidateCase(fixture));
    }

    [Fact]
    public void Loader_RejectsOversizedManifestAndCaseBeforeParsingOrHashing()
    {
        using var oversizedManifest = new TemporaryCorpusDirectory();
        oversizedManifest.SetLength(
            "manifest.v1.json",
            CorpusResourceLimitsV1.MaximumManifestBytes + 1L);

        CorpusLoadException manifestException = Assert.Throws<CorpusLoadException>(
            () => ExpressionCorpusLoader.Load(oversizedManifest.Path));
        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, manifestException.Code);

        using var oversizedCase = new TemporaryCorpusDirectory();
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        const string relativePath = "development/oversized.case.json";
        oversizedCase.WriteCase(@case, relativePath);
        oversizedCase.WriteManifest(@case, relativePath);
        oversizedCase.SetLength(relativePath, CorpusResourceLimitsV1.MaximumCaseBytes + 1L);

        CorpusLoadException caseException = Assert.Throws<CorpusLoadException>(
            () => ExpressionCorpusLoader.Load(oversizedCase.Path));
        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, caseException.Code);
    }

    [Fact]
    public void Loader_UsesTheSameOpaqueCaseFilenameRuleAsSemanticValidation()
    {
        using var directory = new TemporaryCorpusDirectory();
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        const string relativePath = "development/Bad_Name.case.json";
        directory.WriteCase(@case, relativePath);
        directory.WriteManifest(@case, relativePath);

        CorpusLoadException exception = Assert.Throws<CorpusLoadException>(
            () => ExpressionCorpusLoader.Load(directory.Path));

        Assert.Equal(CorpusErrorCode.InvalidManifestPath, exception.Code);
    }

    [Fact]
    public void Loader_RejectsManifestEntryCountAboveTheContractLimit()
    {
        using var directory = new TemporaryCorpusDirectory();
        CorpusManifestEntryV1[] entries = Enumerable.Range(
                0,
                CorpusResourceLimitsV1.MaximumCases + 1)
            .Select(index =>
            {
                string id = $"case-{index:D5}";
                return new CorpusManifestEntryV1(
                    id,
                    CorpusPartitionV1.Development,
                    $"development/{id}.case.json",
                    new string('a', 64),
                    $"session-{index:D5}",
                    CorpusCaseStatusV1.Development,
                    null,
                    null);
            })
            .ToArray();
        byte[] manifest = CorpusJson.SerializeToUtf8Bytes(new CorpusManifestV1(
            CorpusFormatV1.ManifestFormat,
            CorpusFormatV1.SchemaVersion,
            "phase-5.5-v1",
            entries));
        Assert.True(manifest.Length <= CorpusResourceLimitsV1.MaximumManifestBytes);
        directory.WriteBytes("manifest.v1.json", manifest);

        CorpusLoadException exception = Assert.Throws<CorpusLoadException>(
            () => ExpressionCorpusLoader.Load(directory.Path));

        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, exception.Code);
    }

    private sealed class TemporaryCorpusDirectory : IDisposable
    {
        public TemporaryCorpusDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "penumbra-expression-corpus-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "development"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "held-out"));
        }

        public string Path { get; }

        public void WriteCase(ExpressionCaseV1 @case, string relativePath)
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, CorpusJson.SerializeToUtf8Bytes(@case));
        }

        public void WriteManifest(
            ExpressionCaseV1 @case,
            string relativePath,
            string? sha256 = null)
        {
            byte[] caseBytes = File.ReadAllBytes(Resolve(relativePath));
            sha256 ??= Convert.ToHexString(SHA256.HashData(caseBytes)).ToLowerInvariant();
            var manifest = new CorpusManifestV1(
                CorpusFormatV1.ManifestFormat,
                CorpusFormatV1.SchemaVersion,
                @case.CorpusVersion,
                [
                    new CorpusManifestEntryV1(
                        @case.CaseId,
                        @case.Partition,
                        relativePath,
                        sha256,
                        @case.Capture.SessionId,
                        CorpusCaseStatusV1.Development,
                        null,
                        null),
                ]);
            File.WriteAllBytes(
                System.IO.Path.Combine(Path, "manifest.v1.json"),
                CorpusJson.SerializeToUtf8Bytes(manifest));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        public void SetLength(string relativePath, long length)
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using FileStream stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.SetLength(length);
        }

        public void WriteBytes(string relativePath, byte[] bytes)
        {
            string path = Resolve(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        private string Resolve(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
    }
}
