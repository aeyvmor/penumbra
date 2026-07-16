using System.Diagnostics;
using System.Security.Cryptography;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class CorpusResourceBoundaryTests
{
    [Fact]
    public void RepeatedSharedExpectedPage_IsRejectedBeforeDeepValidationOrHashing()
    {
        string longLatex = new('x', CorpusResourceLimitsV1.MaximumTextLength);
        ExpectedPageV1 sharedPage = AcceptedPage(longLatex, repeatedRegionCount: 256);
        ExpressionCaseV1 hostile = CorpusTestData.ValidCase() with
        {
            Steps = Enumerable.Range(0, CorpusResourceLimitsV1.MaximumStepsPerCase)
                .Select(index => (CorpusStepV1)new RecognizeStepV1($"recognize-{index:D4}", sharedPage))
                .ToArray(),
        };
        var stopwatch = Stopwatch.StartNew();

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateCase(hostile);

        stopwatch.Stop();
        CorpusValidationError error = Assert.Single(errors);
        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, error.Code);
        Assert.Equal("case.expectedObservations", error.Location);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
    }

    [Theory]
    [InlineData("reopen")]
    [InlineData("recover")]
    [InlineData("taffy")]
    [InlineData("graph")]
    public void CaseBudget_CountsEveryRepeatedExpectationSurface(string surface)
    {
        ExpectedPageV1 page = RefusedPage(repeatedRegionCount: 256);
        ExpectedSheetV1 sheet = LargeSharedSheet();
        ExpectedGraphSampleV1[] graphSamples = Enumerable.Range(
                0,
                CorpusResourceLimitsV1.MaximumGraphAnchors)
            .Select(index => new ExpectedGraphSampleV1(index, index, 0.01))
            .ToArray();
        CorpusStepV1[] steps = Enumerable.Range(0, CorpusResourceLimitsV1.MaximumStepsPerCase)
            .Select(index => BuildExpectationStep(surface, index, page, sheet, graphSamples))
            .ToArray();
        ExpressionCaseV1 hostile = CorpusTestData.ValidCase() with { Steps = steps };

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateCase(hostile);

        CorpusValidationError error = Assert.Single(errors);
        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, error.Code);
        Assert.Equal("case.expectedObservations", error.Location);
    }

    [Fact]
    public void SuiteBudget_IsCumulativeAcrossIndividuallyBoundedCases()
    {
        ExpectedGraphSampleV1[] samples = Enumerable.Range(
                0,
                CorpusResourceLimitsV1.MaximumGraphAnchors)
            .Select(index => new ExpectedGraphSampleV1(index, index, 0.01))
            .ToArray();
        CorpusStepV1[] firstSteps = GraphSteps(samples, count: 2_000);
        CorpusStepV1[] secondSteps = GraphSteps(samples, count: 2_000);
        ExpressionCaseV1 first = CorpusTestData.ValidCase("dev-budget-001", "session-budget-001") with
        {
            Steps = firstSteps,
        };
        ExpressionCaseV1 second = CorpusTestData.ValidCase("dev-budget-002", "session-budget-002") with
        {
            Steps = secondSteps,
        };

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateSuite(
            UncheckedSuite(first, second));

        CorpusValidationError error = Assert.Single(errors);
        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, error.Code);
        Assert.Equal("cases", error.Location);
    }

    [Fact]
    public void CanonicalStreamingHash_MatchesExistingCanonicalBytesAndHonorsItsCap()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        string expectedCaseHash = Convert.ToHexString(
            SHA256.HashData(CorpusJson.SerializeToUtf8Bytes(@case))).ToLowerInvariant();
        string expectedManifestHash = Convert.ToHexString(
            SHA256.HashData(CorpusJson.SerializeToUtf8Bytes(suite.Manifest))).ToLowerInvariant();

        Assert.True(CorpusJson.TryComputeCanonicalSha256(
            @case, CorpusResourceLimitsV1.MaximumCaseBytes, out string caseHash));
        Assert.True(CorpusJson.TryComputeCanonicalSha256(
            suite.Manifest, CorpusResourceLimitsV1.MaximumManifestBytes, out string manifestHash));
        Assert.Equal(expectedCaseHash, caseHash);
        Assert.Equal(expectedManifestHash, manifestHash);
        Assert.False(CorpusJson.TryComputeCanonicalSha256(@case, maximumBytes: 16, out string cappedHash));
        Assert.Equal(string.Empty, cappedHash);
    }

    [Fact]
    public void Validator_ReportsResourceLimitWhenCanonicalCaseExceedsByteCap()
    {
        string longLatex = new('x', CorpusResourceLimitsV1.MaximumTextLength);
        ExpectedPageV1 sharedPage = AcceptedPage(longLatex, repeatedRegionCount: 1);
        ExpressionCaseV1 oversized = CorpusTestData.ValidCase("dev-oversized-001", "session-oversized-001") with
        {
            Steps = Enumerable.Range(0, CorpusResourceLimitsV1.MaximumStepsPerCase)
                .Select(index => (CorpusStepV1)new RecognizeStepV1($"recognize-{index:D4}", sharedPage))
                .ToArray(),
        };

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateSuite(
            UncheckedSuite(oversized));

        CorpusValidationError error = Assert.Single(errors);
        Assert.Equal(CorpusErrorCode.ResourceLimitExceeded, error.Code);
        Assert.Equal("manifest.entries[0].sha256", error.Location);
    }

    [Fact]
    public void CanonicalManifestHash_StopsAtManifestByteCap()
    {
        string logicalId = new('a', 64);
        string relativePath = $"development/{new string('a', 60)}.case.json";
        CorpusManifestEntryV1 sharedEntry = new(
            logicalId,
            CorpusPartitionV1.Development,
            relativePath,
            new string('a', 64),
            logicalId,
            CorpusCaseStatusV1.Development,
            null,
            null);
        var oversized = new CorpusManifestV1(
            CorpusFormatV1.ManifestFormat,
            CorpusFormatV1.SchemaVersion,
            "phase-5.5-v1",
            Enumerable.Repeat(sharedEntry, CorpusResourceLimitsV1.MaximumCases).ToArray());

        Assert.False(CorpusJson.TryComputeCanonicalSha256(
            oversized,
            CorpusResourceLimitsV1.MaximumManifestBytes,
            out string hash));
        Assert.Equal(string.Empty, hash);
    }

    private static ExpectedPageV1 AcceptedPage(string latex, int repeatedRegionCount)
    {
        RecognizeStepV1 baseline = CorpusTestData.AcceptedStep();
        ExpectedRegionV1 baselineRegion = baseline.Expected.Regions[0];
        AcceptedRegionExpectationV1 accepted = Assert.IsType<AcceptedRegionExpectationV1>(
            baselineRegion.Expectation);
        ExpectedRegionV1 region = baselineRegion with
        {
            Expectation = accepted with
            {
                Latex = latex,
                Tokens = accepted.Tokens
                    .Select(token => token with { Latex = latex })
                    .ToArray(),
                Cas = new ExpectedEvaluationV1(CorpusEvaluationKindV1.Symbolic, true, latex),
            },
        };
        return new ExpectedPageV1(
            Enumerable.Repeat(region, repeatedRegionCount).ToArray(),
            null);
    }

    private static ExpectedPageV1 RefusedPage(int repeatedRegionCount)
    {
        ExpectedRegionV1 baselineRegion = CorpusTestData.AcceptedStep().Expected.Regions[0];
        ExpectedRegionV1 region = baselineRegion with
        {
            Expectation = new RefusedRegionExpectationV1(
                CorpusFailureCategoryV1.SpatialRelation,
                CorpusRefusalCodeV1.SpatialAmbiguity),
        };
        return new ExpectedPageV1(
            Enumerable.Repeat(region, repeatedRegionCount).ToArray(),
            null);
    }

    private static ExpectedSheetV1 LargeSharedSheet()
    {
        string[] freeVariables = Enumerable.Repeat(
                "x",
                CorpusResourceLimitsV1.MaximumTokensPerRegion)
            .ToArray();
        var node = new ExpectedSheetNodeV1(
            "region-1",
            CorpusSheetRoleV1.Statement,
            null,
            freeVariables,
            false,
            null);
        return new ExpectedSheetV1(Enumerable.Repeat(node, 64).ToArray(), [], []);
    }

    private static CorpusStepV1 BuildExpectationStep(
        string surface,
        int index,
        ExpectedPageV1 page,
        ExpectedSheetV1 sheet,
        ExpectedGraphSampleV1[] graphSamples) => surface switch
        {
            "reopen" => new ReopenStepV1(
                $"reopen-{index:D4}",
                "slot-1",
                CorpusOpenStatusV1.OpenedCurrent,
                page),
            "recover" => new RecoverStepV1(
                $"recover-{index:D4}",
                "slot-1",
                CorpusRecoveryDamageV1.CorruptCurrent,
                CorpusOpenStatusV1.BackupRecoveryCandidate,
                page),
            "taffy" => new TaffyProbeStepV1(
                $"taffy-{index:D4}",
                "region-1",
                [],
                ["stroke-a"],
                new CorpusPointV1(0, 0),
                1,
                1,
                "2",
                sheet),
            "graph" => GraphStep(index, graphSamples),
            _ => throw new ArgumentOutOfRangeException(nameof(surface)),
        };

    private static CorpusStepV1[] GraphSteps(ExpectedGraphSampleV1[] samples, int count) =>
        Enumerable.Range(0, count)
            .Select(index => (CorpusStepV1)GraphStep(index, samples))
            .ToArray();

    private static GraphStepV1 GraphStep(int index, ExpectedGraphSampleV1[] samples) => new(
        $"graph-{index:D4}",
        "region-1",
        0,
        CorpusResourceLimitsV1.MaximumGraphAnchors,
        CorpusResourceLimitsV1.MaximumGraphSamples,
        CorpusGraphDecisionV1.Graph,
        "x",
        samples);

    private static ExpressionCorpusSuite UncheckedSuite(params ExpressionCaseV1[] cases)
    {
        CorpusManifestEntryV1[] entries = cases.Select((@case, index) => new CorpusManifestEntryV1(
            @case.CaseId,
            @case.Partition,
            $"development/{index:D4}.case.json",
            new string((char)('a' + index), 64),
            @case.Capture.SessionId,
            CorpusCaseStatusV1.Development,
            null,
            null)).ToArray();
        return new ExpressionCorpusSuite(
            new CorpusManifestV1(
                CorpusFormatV1.ManifestFormat,
                CorpusFormatV1.SchemaVersion,
                "phase-5.5-v1",
                entries),
            cases,
            new string('f', 64));
    }
}
