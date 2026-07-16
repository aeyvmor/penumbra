using System.Security.Cryptography;
using System.Text;
using Penumbra.Core;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class ManifestAndRunnerTests
{
    [Fact]
    public void RuntimeBoundary_DoesNotReceiveCaseIdentityOrExpectedAnswers()
    {
        Type[] createParameters = typeof(IExpressionScenarioRuntimeFactory)
            .GetMethod(nameof(IExpressionScenarioRuntimeFactory.Create))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Type applyParameter = typeof(IExpressionScenarioRuntime)
            .GetMethod(nameof(IExpressionScenarioRuntime.ApplyAsync))!
            .GetParameters()[0]
            .ParameterType;

        Assert.Equal(typeof(ExpressionScenarioInputV1), createParameters[0]);
        Assert.Equal(typeof(ScenarioActionV1), applyParameter);
        Assert.DoesNotContain(typeof(ExpressionScenarioInputV1).GetProperties(), property =>
            property.Name.Contains("Case", StringComparison.Ordinal)
            || property.Name.Contains("Session", StringComparison.Ordinal)
            || property.Name.Contains("Writer", StringComparison.Ordinal)
            || property.Name.Contains("Expected", StringComparison.Ordinal));
        Assert.Empty(typeof(RecognizeActionV1).GetProperties());
        Type[] actions = typeof(ScenarioActionV1).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && type.IsAssignableTo(typeof(ScenarioActionV1)))
            .ToArray();
        Assert.DoesNotContain(actions.SelectMany(type => type.GetProperties()), property =>
            property.Name.Contains("Expected", StringComparison.Ordinal)
            || property.Name.Contains("Output", StringComparison.Ordinal)
            || property.Name.Contains("Alias", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(StampActionV1).GetProperties(), property =>
            property.Name.Contains("Stroke", StringComparison.Ordinal)
            || property.Name.Contains("Scale", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(GraphActionV1).GetProperties(), property =>
            property.Name.Contains("Stroke", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidDevelopmentSuite_HasNoValidationErrors()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();

        Assert.Empty(CorpusValidator.ValidateSuite(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null))));
    }

    [Fact]
    public async Task SuiteFingerprintMustMatchCanonicalManifestBeforeItCanGate()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite valid = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        ExpressionCorpusSuite forged = valid with { ManifestSha256 = new string('f', 64) };

        Assert.Contains(CorpusValidator.ValidateSuite(forged),
            error => error.Code == CorpusErrorCode.InvalidContentHash
                && error.Location == "manifestSha256");
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            forged,
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
            {
                [@case.CaseId] = CorpusTestData.ExactActual(),
            }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);
        Assert.False(report.ProfilePassed);
        Assert.Equal(0, report.ValidatedCaseCount);
        Assert.Equal(new string('0', 64), report.CorpusFingerprint);
    }

    [Fact]
    public void SuiteValidator_StopsAtTheCumulativeSampleBudget()
    {
        CorpusSampleV1[] sharedSamples = Enumerable.Range(0, 500_001)
            .Select(index => new CorpusSampleV1(index % 2 * 10, index % 2 * 10, index, 0.5))
            .ToArray();
        ExpressionCaseV1 first = CorpusTestData.ValidCase() with
        {
            Strokes =
            [
                CorpusTestData.ValidCase().Strokes[0] with { Samples = sharedSamples },
                CorpusTestData.ValidCase().Strokes[1],
            ],
        };
        ExpressionCaseV1 second = first with
        {
            CaseId = "dev-budget-002",
            Capture = first.Capture with { SessionId = "session-budget-002" },
        };
        var manifest = new CorpusManifestV1(
            CorpusFormatV1.ManifestFormat,
            CorpusFormatV1.SchemaVersion,
            "phase-5.5-v1",
            [
                new CorpusManifestEntryV1(
                    first.CaseId,
                    first.Partition,
                    "development/first.case.json",
                    new string('a', 64),
                    first.Capture.SessionId,
                    CorpusCaseStatusV1.Development,
                    null,
                    null),
                new CorpusManifestEntryV1(
                    second.CaseId,
                    second.Partition,
                    "development/second.case.json",
                    new string('b', 64),
                    second.Capture.SessionId,
                    CorpusCaseStatusV1.Development,
                    null,
                    null),
            ]);
        var suite = new ExpressionCorpusSuite(manifest, [first, second], new string('c', 64));

        Assert.Contains(CorpusValidator.ValidateSuite(suite),
            error => error.Code == CorpusErrorCode.ResourceLimitExceeded
                && error.Location == "cases");
    }

    [Fact]
    public void SuiteValidator_RejectsNullHostileEntriesWithoutThrowing()
    {
        var suite = new ExpressionCorpusSuite(
            new CorpusManifestV1(
                CorpusFormatV1.ManifestFormat,
                CorpusFormatV1.SchemaVersion,
                "phase-5.5-v1",
                [null!]),
            [null!],
            new string('0', 64));

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateSuite(suite);

        Assert.Contains(errors, error => error.Code == CorpusErrorCode.MissingValue);
    }

    [Fact]
    public void SuiteValidator_RejectsNullManifestKeysWithoutThrowing()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite baseline = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        CorpusManifestEntryV1 entry = baseline.Manifest.Entries[0] with { CaseId = null! };
        ExpressionCorpusSuite hostile = baseline with
        {
            Manifest = baseline.Manifest with { Entries = [entry] },
        };

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateSuite(hostile);

        Assert.Contains(errors, error => error.Code == CorpusErrorCode.MissingValue);
    }

    [Fact]
    public void SuiteValidator_RejectsSessionAndNormalizedInkAcrossPartitions()
    {
        ExpressionCaseV1 development = CorpusTestData.ValidCase();
        ExpressionCaseV1 heldOut = development with
        {
            CaseId = "heldout-001",
            Partition = CorpusPartitionV1.HeldOut,
        };
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (development, CorpusCaseStatusV1.Development, null),
            (heldOut, CorpusCaseStatusV1.Frozen, null));

        CorpusErrorCode[] codes = CorpusValidator.ValidateSuite(suite).Select(error => error.Code).ToArray();

        Assert.Contains(CorpusErrorCode.CrossPartitionSession, codes);
        Assert.Contains(CorpusErrorCode.CrossPartitionDuplicateInk, codes);
    }

    [Fact]
    public void ContaminatedHeldOutCase_RequiresDistinctActiveReplacement()
    {
        ExpressionCaseV1 heldOut = CorpusTestData.ValidCase() with
        {
            CaseId = "heldout-001",
            Partition = CorpusPartitionV1.HeldOut,
            Capture = CorpusTestData.ValidCase().Capture with { SessionId = "session-heldout-001" },
        };
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (heldOut, CorpusCaseStatusV1.Contaminated, "heldout-002"));

        Assert.Contains(CorpusValidator.ValidateSuite(suite),
            error => error.Code == CorpusErrorCode.MissingHeldOutReplacement);
    }

    [Fact]
    public async Task Runner_CountsExactUnexpectedRefusalAndAcceptedWrongWithoutDoubleCounting()
    {
        ExpressionCaseV1 exact = CorpusTestData.ValidCase();
        ExpressionCaseV1 refused = CorpusTestData.ValidCase("dev-refusal-001", "session-refusal-001") with
        {
            Steps = [CorpusTestData.ExpectedRefusalStep()],
        };
        ExpressionCaseV1 wrong = CorpusTestData.ValidCase("dev-wrong-001", "session-wrong-001");
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (exact, CorpusCaseStatusV1.Development, null),
            (refused, CorpusCaseStatusV1.Development, null),
            (wrong, CorpusCaseStatusV1.Development, null));

        var factory = new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
        {
            [exact.CaseId] = CorpusTestData.ExactActual(),
            [refused.CaseId] = CorpusTestData.RefusedActual(),
            [wrong.CaseId] = CorpusTestData.WrongLabelAndLatexActual(),
        });
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite, factory, new CorpusRunOptions(CorpusPartitionV1.Development, MetricsCapacity: 64), default);

        Assert.Equal(2, report.ExactExpressionDenominator);
        Assert.Equal(1, report.ExactExpressionNumerator);
        Assert.Equal(1, report.AcceptedWrongCount);
        Assert.Equal(1, report.RefusalCount);
        Assert.Equal(1, report.ExpectedRefusalPassCount);
        Assert.Equal(0, report.UnexpectedRefusalCount);
        Assert.Equal(1, report.Failures[CorpusFailureCategoryV1.SymbolClassification]);
    }

    [Fact]
    public async Task Runner_ExactEvidenceWithRequiredMetricsPasses()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
            {
                [@case.CaseId] = CorpusTestData.ExactActual(),
            }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);

        Assert.True(report.ProfilePassed);
        Assert.False(report.ReadinessPassed);
        Assert.Equal(CorpusRunProfileV1.DiagnosticAccuracy, report.Profile);
        Assert.True(report.InfrastructureValid);
        Assert.Equal(1, report.ExactExpressionNumerator);
        Assert.Equal(1d, report.ExactExpressionRate);
    }

    [Fact]
    public async Task Runner_DiagnosticAccuracyCannotClaimSlice3ReadinessOrIgnoreLatencyBudgets()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new FakeRuntimeFactory(
                new Dictionary<string, StepActualV1> { [@case.CaseId] = CorpusTestData.ExactActual() },
                metricsToRecord: 300),
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                MetricsCapacity: 512,
                Profile: CorpusRunProfileV1.Slice3DevelopmentReadiness),
            default);

        Assert.True(report.AccuracyPassed);
        Assert.False(report.CoveragePassed);
        Assert.False(report.LatencyPassed);
        Assert.False(report.ProfilePassed);
        Assert.False(report.ReadinessPassed);
        Assert.True(report.MissingCoverageFeatureCount > 0);
        Assert.True(report.LatencyBudgetViolationCount > 0);
        Assert.DoesNotContain("gatePassed", CorpusReportJson.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_GateProfileCannotBeWeakenedByOptions()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        var factory = new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
        {
            [@case.CaseId] = CorpusTestData.ExactActual(),
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ExpressionCorpusRunner().RunAsync(
            suite,
            factory,
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                64,
                RequiredExactExpressionRate: 0),
            default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ExpressionCorpusRunner().RunAsync(
            suite,
            factory,
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                CorpusResourceLimitsV1.MaximumMetricCapacity + 1),
            default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new ExpressionCorpusRunner().RunAsync(
            suite,
            factory,
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                64,
                RequireMetricCoverage: false,
                Profile: CorpusRunProfileV1.Slice3DevelopmentReadiness),
            default));

        CorpusRunReport diagnostic = await new ExpressionCorpusRunner().RunAsync(
            suite,
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
            {
                [@case.CaseId] = CorpusTestData.ExactActual(),
            }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64, RequireMetricCoverage: false),
            default);
        Assert.Equal(CorpusRunProfileV1.DiagnosticAccuracy, diagnostic.Profile);
        Assert.True(diagnostic.ProfilePassed);
    }

    [Fact]
    public async Task Runner_CachesAndSanitizesRuntimeIdentity()
    {
        const double shippedModelThreshold = 0.5924550294876099;
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        var factory = new IdentityRuntimeFactory(
            CorpusTestData.ExactActual(),
            pipeline: () => "private-pipeline-canary",
            model: () => new string('b', 64),
            threshold: () => shippedModelThreshold);

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite,
            factory,
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);
        string json = CorpusReportJson.Serialize(report);

        Assert.True(report.ProfilePassed);
        Assert.Equal(64, report.PipelineFingerprint.Length);
        Assert.DoesNotContain("private-pipeline-canary", json, StringComparison.Ordinal);
        Assert.Equal(shippedModelThreshold, report.RecognitionThreshold);
        Assert.Equal(1, factory.PipelineReadCount);
        Assert.Equal(1, factory.ModelReadCount);
        Assert.Equal(1, factory.ThresholdReadCount);
    }

    [Fact]
    public async Task Runner_InvalidIdentitySentinelsAndThresholdCannotGate()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        var zeroModel = new IdentityRuntimeFactory(
            CorpusTestData.ExactActual(),
            pipeline: () => "test-pipeline-v1",
            model: () => new string('0', 64),
            threshold: () => 0.55);
        var zeroThreshold = new IdentityRuntimeFactory(
            CorpusTestData.ExactActual(),
            pipeline: () => "test-pipeline-v1",
            model: () => new string('b', 64),
            threshold: () => 0);

        CorpusRunReport zeroModelReport = await new ExpressionCorpusRunner().RunAsync(
            suite, zeroModel, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);
        CorpusRunReport zeroThresholdReport = await new ExpressionCorpusRunner().RunAsync(
            suite, zeroThreshold, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);

        Assert.False(zeroModelReport.InfrastructureValid);
        Assert.False(zeroModelReport.ProfilePassed);
        Assert.Equal(new string('0', 64), zeroModelReport.ModelFingerprint);
        Assert.False(zeroThresholdReport.InfrastructureValid);
        Assert.False(zeroThresholdReport.ProfilePassed);
        Assert.Null(zeroThresholdReport.RecognitionThreshold);
    }

    [Fact]
    public async Task Runner_ThrowingOrNullIdentityGettersFailClosedWithoutEscaping()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        var throwing = new IdentityRuntimeFactory(
            CorpusTestData.ExactActual(),
            pipeline: () => throw new InvalidOperationException("private-path-canary"),
            model: () => new string('b', 64),
            threshold: () => 0.55);
        var nullPipeline = new IdentityRuntimeFactory(
            CorpusTestData.ExactActual(),
            pipeline: () => null,
            model: () => new string('b', 64),
            threshold: () => 0.55);

        CorpusRunReport throwingReport = await new ExpressionCorpusRunner().RunAsync(
            suite, throwing, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);
        CorpusRunReport nullReport = await new ExpressionCorpusRunner().RunAsync(
            suite, nullPipeline, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);

        Assert.False(throwingReport.InfrastructureValid);
        Assert.False(throwingReport.ProfilePassed);
        Assert.Equal(new string('0', 64), throwingReport.PipelineFingerprint);
        Assert.DoesNotContain("private-path-canary", CorpusReportJson.Serialize(throwingReport));
        Assert.False(nullReport.InfrastructureValid);
        Assert.False(nullReport.ProfilePassed);
    }

    [Fact]
    public async Task Runner_InvalidActualConfidenceAndOversizedPageAreInfrastructureFailures()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        RecognizeActualV1 exact = CorpusTestData.ExactActual();
        ActualRegionV1 exactRegion = exact.Actual.Regions[0];
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(exactRegion.Outcome);
        RecognizeActualV1 invalidConfidence = exact with
        {
            Actual = exact.Actual with
            {
                Regions =
                [
                    exactRegion with
                    {
                        Outcome = accepted with
                        {
                            Tokens =
                            [
                                accepted.Tokens[0] with { Confidence = 2 },
                                accepted.Tokens[1],
                            ],
                        },
                    },
                ],
            },
        };
        RecognizeActualV1 oversized = exact with
        {
            Actual = exact.Actual with
            {
                Regions = Enumerable.Repeat(
                        exactRegion,
                        CorpusResourceLimitsV1.MaximumRegionsPerPage + 1)
                    .ToArray(),
            },
        };
        RecognizeActualV1 overlong = exact with
        {
            Actual = exact.Actual with
            {
                Regions =
                [
                    exactRegion with
                    {
                        Outcome = accepted with
                        {
                            Latex = new string('x', CorpusResourceLimitsV1.MaximumTextLength + 1),
                            Cas = new ExpectedEvaluationV1(
                                CorpusEvaluationKindV1.Pending,
                                true,
                                "pending"),
                        },
                    },
                ],
            },
        };

        CorpusRunReport confidenceReport = await RunSingleAsync(suite, @case, invalidConfidence);
        CorpusRunReport oversizedReport = await RunSingleAsync(suite, @case, oversized);
        CorpusRunReport textReport = await RunSingleAsync(suite, @case, overlong);

        Assert.False(confidenceReport.InfrastructureValid);
        Assert.False(confidenceReport.ProfilePassed);
        Assert.True(confidenceReport.Failures[CorpusFailureCategoryV1.Infrastructure] > 0);
        Assert.False(oversizedReport.InfrastructureValid);
        Assert.False(oversizedReport.ProfilePassed);
        Assert.False(textReport.InfrastructureValid);
        Assert.False(textReport.ProfilePassed);
    }

    [Fact]
    public async Task Runner_EmptyOrCheckpointFreePartitionsCannotPass()
    {
        var emptyManifest = new CorpusManifestV1(
            CorpusFormatV1.ManifestFormat,
            CorpusFormatV1.SchemaVersion,
            "phase-5.5-v1",
            []);
        var emptySuite = new ExpressionCorpusSuite(emptyManifest, [], new string('c', 64));
        CorpusRunReport empty = await new ExpressionCorpusRunner().RunAsync(
            emptySuite,
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1>()),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);

        Assert.False(empty.ProfilePassed);
        Assert.Null(empty.ExactExpressionRate);

        ExpressionCaseV1 noCheckpoint = CorpusTestData.ValidCase() with { Steps = [] };
        CorpusRunReport noCheckpointReport = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((noCheckpoint, CorpusCaseStatusV1.Development, null)),
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
            {
                [noCheckpoint.CaseId] = CorpusTestData.ExactActual(),
            }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);

        Assert.False(noCheckpointReport.ProfilePassed);
        Assert.Equal(0, noCheckpointReport.CheckpointCount);
    }

    [Fact]
    public async Task Runner_DerivesLowConfidenceAndOodAcceptedLabelsAsRefusals()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        RecognizeActualV1 exact = CorpusTestData.ExactActual();
        ActualRegionV1 region = exact.Actual.Regions[0];
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(region.Outcome);
        RecognizeActualV1 lowConfidence = exact with
        {
            Actual = exact.Actual with
            {
                Regions =
                [
                    region with
                    {
                        Outcome = accepted with
                        {
                            Tokens =
                            [
                                accepted.Tokens[0] with { Confidence = 0.1 },
                                accepted.Tokens[1] with { Rejected = true },
                            ],
                        },
                    },
                ],
            },
        };
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1> { [@case.CaseId] = lowConfidence }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);

        Assert.Equal(0, report.AcceptedCount);
        Assert.Equal(1, report.RefusalCount);
        Assert.Equal(1, report.UnexpectedRefusalCount);
        Assert.False(report.ProfilePassed);
    }

    [Fact]
    public async Task Runner_DuplicateActualOwnershipAndWrongBoundsCannotPassAsExact()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        RecognizeActualV1 exact = CorpusTestData.ExactActual();
        ActualRegionV1 region = exact.Actual.Regions[0];
        RecognizeActualV1 hostile = exact with
        {
            Actual = exact.Actual with
            {
                Regions =
                [
                    region with
                    {
                        StrokeIds = ["stroke-a", "stroke-a", "stroke-b"],
                        Bounds = new CorpusBoundsV1(100, 0, 30, 10),
                    },
                ],
            },
        };
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1> { [@case.CaseId] = hostile }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);

        Assert.Equal(0, report.ExactExpressionNumerator);
        Assert.Equal(0, report.AcceptedWrongCount);
        Assert.False(report.InfrastructureValid);
        Assert.Equal(1, report.Failures[CorpusFailureCategoryV1.Infrastructure]);
        Assert.Equal(1, report.StructuralMismatchCount);
        Assert.False(report.ProfilePassed);
    }

    [Fact]
    public async Task Runner_UnexpectedActualSheetCannotPassWhenExpectedSheetIsAbsent()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        RecognizeActualV1 exact = CorpusTestData.ExactActual();
        RecognizeActualV1 staleSheet = exact with
        {
            Actual = exact.Actual with
            {
                Sheet = new ActualSheetV1(
                    [
                        new ActualSheetNodeV1(
                            "runtime-region-1",
                            ["stroke-a", "stroke-b"],
                            CorpusSheetRoleV1.Query,
                            null,
                            [],
                            false,
                            null),
                    ],
                    [],
                    []),
            },
        };

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
            {
                [@case.CaseId] = staleSheet,
            }),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64),
            default);

        Assert.False(report.ProfilePassed);
        Assert.Equal(1, report.Failures[CorpusFailureCategoryV1.Sheet]);
        Assert.Equal(1, report.AcceptedWrongCount);
    }

    [Fact]
    public async Task Runner_WrongActualTypeForNonRecognitionStepFailsClosed()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Steps =
            [
                new SaveStepV1("save-1", "page", CorpusSaveModeV1.Explicit),
                CorpusTestData.AcceptedStep(),
            ],
        };
        var factory = new SequenceRuntimeFactory(CorpusTestData.ExactActual(), CorpusTestData.ExactActual());

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            factory,
            new CorpusRunOptions(CorpusPartitionV1.Development, 64, RequireMetricCoverage: false),
            default);

        Assert.False(report.InfrastructureValid);
        Assert.True(report.Failures[CorpusFailureCategoryV1.Infrastructure] >= 1);
        Assert.Equal(0, report.ExecutedCaseCount);
    }

    [Fact]
    public async Task Runner_GraphAnchorsRequireDistinctFiniteActualSamples()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Steps =
            [
                CorpusTestData.AcceptedStep(),
                new GraphStepV1(
                    "graph-1",
                    "region-1",
                    -1,
                    1,
                    2,
                    CorpusGraphDecisionV1.Graph,
                    "x",
                    [
                        new ExpectedGraphSampleV1(0, 0, 0.1),
                        new ExpectedGraphSampleV1(0.05, 0, 0.1),
                    ]),
            ],
        };
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        var reusedSample = new GraphActualV1(
            CorpusGraphDecisionV1.Graph,
            "x",
            [new ActualGraphSampleV1(0.04, 0), new ActualGraphSampleV1(1, 10)]);
        var nonFinite = new GraphActualV1(
            CorpusGraphDecisionV1.Graph,
            "x",
            [new ActualGraphSampleV1(0, 0), new ActualGraphSampleV1(double.NaN, 0)]);

        CorpusRunReport reusedReport = await new ExpressionCorpusRunner().RunAsync(
            suite,
            new SequenceRuntimeFactory(CorpusTestData.ExactActual(), reusedSample),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64, RequireMetricCoverage: false),
            default);
        CorpusRunReport nonFiniteReport = await new ExpressionCorpusRunner().RunAsync(
            suite,
            new SequenceRuntimeFactory(CorpusTestData.ExactActual(), nonFinite),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64, RequireMetricCoverage: false),
            default);

        Assert.Equal(1, reusedReport.Failures[CorpusFailureCategoryV1.Graph]);
        Assert.False(reusedReport.ProfilePassed);
        Assert.False(nonFiniteReport.InfrastructureValid);
        Assert.True(nonFiniteReport.Failures[CorpusFailureCategoryV1.Infrastructure] > 0);
    }

    [Fact]
    public async Task Runner_NullTaffySheetIsAnInfrastructureContractFailure()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Steps =
            [
                CorpusTestData.AcceptedStep(),
                new TaffyProbeStepV1(
                    "taffy-1",
                    "region-1",
                    [new LayoutPathSegmentV1(LayoutRoleV1.Item, 0)],
                    ["stroke-a"],
                    new CorpusPointV1(0, 0),
                    10,
                    1,
                    "2",
                    new ExpectedSheetV1([], [], [])),
            ],
        };
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite,
            new SequenceRuntimeFactory(
                CorpusTestData.ExactActual(),
                new TaffyProbeActualV1("2", null!)),
            new CorpusRunOptions(CorpusPartitionV1.Development, 64, RequireMetricCoverage: false),
            default);

        Assert.False(report.InfrastructureValid);
        Assert.False(report.ProfilePassed);
        Assert.True(report.Failures[CorpusFailureCategoryV1.Infrastructure] > 0);
    }

    [Fact]
    public async Task Runner_HeldOutProfileAllowsCategorizedRefusalsAboveEightyFivePercentExact()
    {
        var cases = new List<(ExpressionCaseV1 Case, CorpusCaseStatusV1 Status, string? Replacement)>();
        var results = new Dictionary<string, StepActualV1>();
        for (int index = 0; index < 10; index++)
        {
            ExpressionCaseV1 @case = CorpusTestData.WithSecondStrokeEndY(
                CorpusTestData.ValidCase(
                    $"heldout-{index:D3}",
                    $"session-heldout-{index:D3}"),
                10 - index * 0.5) with
            {
                Partition = CorpusPartitionV1.HeldOut,
            };
            cases.Add((@case, CorpusCaseStatusV1.Frozen, null));
            results[@case.CaseId] = index == 9 ? CorpusTestData.RefusedActual() : CorpusTestData.ExactActual();
        }

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite(cases.ToArray()),
            new FakeRuntimeFactory(results),
            new CorpusRunOptions(CorpusPartitionV1.HeldOut, 128),
            default);

        Assert.Equal(0.9, report.ExactExpressionRate);
        Assert.Equal(1, report.UnexpectedRefusalCount);
        Assert.Equal(0, report.AcceptedWrongCount);
        Assert.True(report.ProfilePassed);
    }

    [Fact]
    public async Task Runner_HeldOutProfileNeverTreatsAMissingAcceptedRegionAsARefusal()
    {
        var cases = new List<(ExpressionCaseV1 Case, CorpusCaseStatusV1 Status, string? Replacement)>();
        var results = new Dictionary<string, StepActualV1>();
        for (int index = 0; index < 10; index++)
        {
            ExpressionCaseV1 @case = CorpusTestData.WithSecondStrokeEndY(
                CorpusTestData.ValidCase(
                    $"heldout-missing-{index:D2}",
                    $"session-heldout-missing-{index:D2}"),
                10 - index * 0.5) with
            {
                Partition = CorpusPartitionV1.HeldOut,
            };
            cases.Add((@case, CorpusCaseStatusV1.Frozen, null));
            results[@case.CaseId] = index == 9
                ? new RecognizeActualV1(new ActualPageV1([], null))
                : CorpusTestData.ExactActual();
        }

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite(cases.ToArray()),
            new FakeRuntimeFactory(results),
            new CorpusRunOptions(CorpusPartitionV1.HeldOut, 128),
            default);

        Assert.Equal(0.9, report.ExactExpressionRate);
        Assert.True(report.StructuralMismatchCount > 0);
        Assert.False(report.ProfilePassed);
    }

    [Fact]
    public async Task Runner_ExpectedAcceptanceRefusedIsUnexpectedRefusal()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null));
        var factory = new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
        {
            [@case.CaseId] = CorpusTestData.RefusedActual(),
        });

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite, factory, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);

        Assert.Equal(1, report.RefusalCount);
        Assert.Equal(1, report.UnexpectedRefusalCount);
        Assert.Equal(0, report.AcceptedWrongCount);
        Assert.False(report.ProfilePassed);
    }

    [Fact]
    public async Task Runner_AllActualRefusalsAreCountedEvenWhenReasonIsWrong()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Steps = [CorpusTestData.ExpectedRefusalStep()],
        };
        ExpressionCorpusSuite suite = CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null));
        var actual = new RecognizeActualV1(
            new ActualPageV1(
            [
                new ActualRegionV1(
                    "runtime-region-1",
                    ["stroke-a", "stroke-b"],
                    new RefusedRegionActualV1(
                        CorpusFailureCategoryV1.SymbolClassification,
                        CorpusRefusalCodeV1.LowConfidence),
                    new CorpusBoundsV1(0, 0, 30, 10)),
            ],
            null));
        var factory = new FakeRuntimeFactory(new Dictionary<string, StepActualV1> { [@case.CaseId] = actual });

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite, factory, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);

        Assert.Equal(1, report.RefusalCount);
        Assert.Equal(0, report.ExpectedRefusalPassCount);
        Assert.Equal(1, report.Failures[CorpusFailureCategoryV1.SymbolClassification]);
    }

    [Fact]
    public async Task Runner_MissingCapabilityInvalidatesRunInsteadOfPassingAsRefusal()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null));
        var factory = new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
        {
            [@case.CaseId] = new CapabilityUnavailableActualV1(CorpusCapabilityV1.RecursiveLayout),
        });

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite, factory, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);

        Assert.False(report.InfrastructureValid);
        Assert.True(report.Failures[CorpusFailureCategoryV1.Infrastructure] >= 1);
        Assert.Equal(1, report.UnavailableCapabilities[CorpusCapabilityV1.RecursiveLayout]);
        Assert.Equal(0, report.RefusalCount);
    }

    [Fact]
    public async Task Runner_MetricOverflowInvalidatesRun()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null));
        var factory = new FakeRuntimeFactory(
            new Dictionary<string, StepActualV1> { [@case.CaseId] = CorpusTestData.ExactActual() },
            metricsToRecord: 3);

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite, factory, new CorpusRunOptions(CorpusPartitionV1.Development, MetricsCapacity: 2), default);

        Assert.False(report.InfrastructureValid);
        Assert.True(report.Failures[CorpusFailureCategoryV1.Infrastructure] >= 1);
        Assert.True(report.MetricObservationDropped > 0);
    }

    [Fact]
    public async Task DefaultReport_DoesNotContainPrivateDiagnosticValues()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase("private-case-canary", "private-session-canary");
        ExpressionCorpusSuite suite = CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null));
        var factory = new FakeRuntimeFactory(new Dictionary<string, StepActualV1>
        {
            [@case.CaseId] = CorpusTestData.WrongLabelAndLatexActual("private-latex-canary"),
        });
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite, factory, new CorpusRunOptions(CorpusPartitionV1.Development, 64), default);

        string json = CorpusReportJson.Serialize(report);

        Assert.DoesNotContain("private-case-canary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session-canary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-latex-canary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-a", json, StringComparison.Ordinal);
    }

    private static Task<CorpusRunReport> RunSingleAsync(
        ExpressionCorpusSuite suite,
        ExpressionCaseV1 @case,
        StepActualV1 actual) => new ExpressionCorpusRunner().RunAsync(
        suite,
        new FakeRuntimeFactory(new Dictionary<string, StepActualV1> { [@case.CaseId] = actual }),
        new CorpusRunOptions(CorpusPartitionV1.Development, 64),
        default);

    private sealed class IdentityRuntimeFactory(
        StepActualV1 result,
        Func<string?> pipeline,
        Func<string?> model,
        Func<double> threshold) : IExpressionScenarioRuntimeFactory
    {
        public int PipelineReadCount { get; private set; }

        public int ModelReadCount { get; private set; }

        public int ThresholdReadCount { get; private set; }

        public string PipelineFingerprint
        {
            get
            {
                PipelineReadCount++;
                return pipeline()!;
            }
        }

        public string ModelFingerprint
        {
            get
            {
                ModelReadCount++;
                return model()!;
            }
        }

        public double RecognitionThreshold
        {
            get
            {
                ThresholdReadCount++;
                return threshold();
            }
        }

        public IExpressionScenarioRuntime Create(ExpressionScenarioInputV1 input, ILocalMetricsSink metrics)
        {
            foreach (MetricOperation operation in new[]
                     {
                         MetricOperation.RecognitionProcessing,
                         MetricOperation.RecognitionPartition,
                         MetricOperation.RecognitionClassification,
                         MetricOperation.RecognitionGrammar,
                     })
            {
                metrics.Record(new MetricObservation(
                    operation,
                    MetricOutcome.Completed,
                    TimeSpan.FromMilliseconds(1),
                    1));
            }
            return new FakeRuntime(result);
        }
    }

    private sealed class FakeRuntimeFactory : IExpressionScenarioRuntimeFactory
    {
        private readonly Queue<StepActualV1> _results;
        private readonly int _metricsToRecord;

        public FakeRuntimeFactory(
            IReadOnlyDictionary<string, StepActualV1> results,
            int metricsToRecord = 0)
        {
            _results = new Queue<StepActualV1>(results.Values);
            _metricsToRecord = metricsToRecord;
        }

        public string PipelineFingerprint => "test-pipeline-v1";

        public string ModelFingerprint => new('a', 64);

        public double RecognitionThreshold => 0.75;

        public IExpressionScenarioRuntime Create(ExpressionScenarioInputV1 input, ILocalMetricsSink metrics)
        {
            foreach (MetricOperation operation in new[]
                     {
                         MetricOperation.RecognitionProcessing,
                         MetricOperation.RecognitionPartition,
                         MetricOperation.RecognitionClassification,
                         MetricOperation.RecognitionGrammar,
                     })
            {
                metrics.Record(new MetricObservation(
                    operation,
                    MetricOutcome.Completed,
                    TimeSpan.FromMilliseconds(1),
                    1));
            }
            for (int i = 0; i < _metricsToRecord; i++)
            {
                metrics.Record(new MetricObservation(
                    MetricOperation.RecognitionProcessing,
                    MetricOutcome.Completed,
                    TimeSpan.FromMilliseconds(i + 1),
                    1));
            }

            return new FakeRuntime(_results.Dequeue());
        }
    }

    private sealed class FakeRuntime(StepActualV1 result) : IExpressionScenarioRuntime
    {
        public Task<StepActualV1> ApplyAsync(ScenarioActionV1 action, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SequenceRuntimeFactory(params StepActualV1[] results) : IExpressionScenarioRuntimeFactory
    {
        public string PipelineFingerprint => "test-pipeline-v1";

        public string ModelFingerprint => new('d', 64);

        public double RecognitionThreshold => 0.75;

        public IExpressionScenarioRuntime Create(ExpressionScenarioInputV1 input, ILocalMetricsSink metrics) =>
            new SequenceRuntime(results);
    }

    private sealed class SequenceRuntime(IEnumerable<StepActualV1> results) : IExpressionScenarioRuntime
    {
        private readonly Queue<StepActualV1> _results = new(results);

        public Task<StepActualV1> ApplyAsync(ScenarioActionV1 action, CancellationToken cancellationToken) =>
            Task.FromResult(_results.Dequeue());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class CorpusTestData
{
    public static ExpressionCaseV1 ValidCase(
        string caseId = "dev-linear-001",
        string sessionId = "session-dev-001") => new(
        Format: CorpusFormatV1.CaseFormat,
        SchemaVersion: CorpusFormatV1.SchemaVersion,
        CorpusVersion: "phase-5.5-v1",
        CaseRevision: 1,
        CaseId: caseId,
        Partition: CorpusPartitionV1.Development,
        Capture: new CaptureMetadataV1(
            Source: CorpusCaptureSourceV1.Synthetic,
            DataClassification: CorpusDataClassificationV1.PublicSynthetic,
            WriterId: "synthetic-writer",
            SessionId: sessionId,
            DeviceClass: CorpusDeviceClassV1.Synthetic,
            PressureMode: CorpusPressureModeV1.Normalized,
            CaptureApi: CorpusCaptureApiV1.HandAuthored,
            CaptureBuild: "synthetic-v1",
            Consent: null),
        Strokes:
        [
            new CorpusStrokeV1("stroke-a", null,
            [
                new CorpusSampleV1(0, 0, 0, 0.5),
                new CorpusSampleV1(10, 10, 10, 0.6),
            ]),
            new CorpusStrokeV1("stroke-b", null,
            [
                new CorpusSampleV1(20, 0, 0, 0.5),
                new CorpusSampleV1(30, 10, 10, 0.6),
            ]),
        ],
        InitialStrokeIds: ["stroke-a", "stroke-b"],
        Steps: [AcceptedStep()]);

    public static CaptureConsentV1 ValidPrivateConsent() => new(
        PolicyVersion: 1,
        Basis: CorpusConsentBasisV1.ExplicitUserCaptureCheckpoint,
        RightsBasis: CorpusRightsBasisV1.UserAuthoredContributorOwned,
        Scopes:
        [
            CorpusConsentScopeV1.PrivateLocalRegression,
            CorpusConsentScopeV1.PrivateGitVersioning,
            CorpusConsentScopeV1.PrivateRemoteBackup,
        ],
        PrivateRemoteStorageAllowed: true,
        PrivateModelTrainingAllowed: false,
        PublicRedistributionAllowed: false,
        RecordedAtUtc: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));

    public static ExpressionCaseV1 WithSecondStrokeEndY(ExpressionCaseV1 @case, double y)
    {
        CorpusStrokeV1 second = @case.Strokes[1] with
        {
            Samples =
            [
                @case.Strokes[1].Samples[0],
                @case.Strokes[1].Samples[1] with { Y = y },
            ],
        };
        return @case with { Strokes = [@case.Strokes[0], second] };
    }

    public static RecognizeStepV1 AcceptedStep() => new(
        "recognize-1",
        new ExpectedPageV1(
        [
            new ExpectedRegionV1(
                "region-1",
                ["stroke-a", "stroke-b"],
                new CorpusBoundsV1(0, 0, 30, 10),
                0.01,
                new AcceptedRegionExpectationV1(
                    "1+",
                    [
                        new ExpectedTokenV1("token-a", "1", ["stroke-a"]),
                        new ExpectedTokenV1("token-b", "+", ["stroke-b"]),
                    ],
                    new ExpectedLayoutNodeV1(
                        LayoutKindV1.Sequence,
                        [],
                        [
                            new ExpectedLayoutEdgeV1(LayoutRoleV1.Item,
                                new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["token-a"], [])),
                            new ExpectedLayoutEdgeV1(LayoutRoleV1.Item,
                                new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["token-b"], [])),
                        ]),
                    null)),
        ],
        null));

    public static RecognizeStepV1 ExpectedRefusalStep() => new(
        "recognize-1",
        new ExpectedPageV1(
        [
            new ExpectedRegionV1(
                "region-1",
                ["stroke-a", "stroke-b"],
                new CorpusBoundsV1(0, 0, 30, 10),
                0.01,
                new RefusedRegionExpectationV1(
                    CorpusFailureCategoryV1.SpatialRelation,
                    CorpusRefusalCodeV1.SpatialAmbiguity)),
        ],
        null));

    public static ExpressionCaseV1 ValidFractionCase()
    {
        ExpressionCaseV1 @case = ValidCase();
        RecognizeStepV1 recognize = Assert.IsType<RecognizeStepV1>(@case.Steps[^1]);
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        var accepted = new AcceptedRegionExpectationV1(
            @"\frac{1}{2}",
            [
                new ExpectedTokenV1("numerator", "1", ["stroke-a"]),
                new ExpectedTokenV1("bar", "-", ["stroke-b"]),
                new ExpectedTokenV1("denominator", "2", ["stroke-c"]),
            ],
            new ExpectedLayoutNodeV1(
                LayoutKindV1.Fraction,
                ["bar"],
                [
                    new ExpectedLayoutEdgeV1(LayoutRoleV1.Numerator,
                        new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["numerator"], [])),
                    new ExpectedLayoutEdgeV1(LayoutRoleV1.Denominator,
                        new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["denominator"], [])),
                ]),
            null);
        @case = @case with
        {
            Strokes =
            [
                .. @case.Strokes,
                new CorpusStrokeV1("stroke-c", null,
                [
                    new CorpusSampleV1(10, 20, 0, 0.5),
                    new CorpusSampleV1(20, 30, 10, 0.5),
                ]),
            ],
            InitialStrokeIds = ["stroke-a", "stroke-b", "stroke-c"],
        };
        return ReplaceExpectedRegion(@case, region with
        {
            StrokeIds = ["stroke-a", "stroke-b", "stroke-c"],
            Expectation = accepted,
        });
    }

    public static ExpressionCaseV1 ReplaceExpectedRegion(ExpressionCaseV1 @case, ExpectedRegionV1 region)
    {
        RecognizeStepV1 recognize = Assert.IsType<RecognizeStepV1>(@case.Steps[^1]);
        return @case with { Steps = [recognize with { Expected = recognize.Expected with { Regions = [region] } }] };
    }

    public static ExpressionCorpusSuite Suite(
        params (ExpressionCaseV1 Case, CorpusCaseStatusV1 Status, string? Replacement)[] cases)
    {
        CorpusManifestEntryV1[] entries = cases.Select((item, index) => new CorpusManifestEntryV1(
            item.Case.CaseId,
            item.Case.Partition,
            $"{(item.Case.Partition == CorpusPartitionV1.Development ? "development" : "held-out")}/{index:D4}.case.json",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CorpusJson.Serialize(item.Case)))).ToLowerInvariant(),
            item.Case.Capture.SessionId,
            item.Status,
            item.Status == CorpusCaseStatusV1.Contaminated ? CorpusContaminationReasonV1.InspectedForFix : null,
            item.Replacement)).ToArray();
        var manifest = new CorpusManifestV1(
            CorpusFormatV1.ManifestFormat,
            CorpusFormatV1.SchemaVersion,
            "phase-5.5-v1",
            entries);
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(CorpusJson.SerializeToUtf8Bytes(manifest))).ToLowerInvariant();
        return new ExpressionCorpusSuite(manifest, cases.Select(item => item.Case).ToArray(), fingerprint);
    }

    public static RecognizeActualV1 ExactActual() => new(
        new ActualPageV1(
        [
            new ActualRegionV1(
                "runtime-region-1",
                ["stroke-a", "stroke-b"],
                new AcceptedRegionActualV1(
                    "1+",
                    [
                        new ActualTokenV1("1", ["stroke-a"], 0.99, false),
                        new ActualTokenV1("+", ["stroke-b"], 0.99, false),
                    ],
                    new ActualLayoutNodeV1(
                        LayoutKindV1.Sequence,
                        [],
                        [
                            new ActualLayoutEdgeV1(LayoutRoleV1.Item,
                                new ActualLayoutNodeV1(LayoutKindV1.Token, [0], [])),
                            new ActualLayoutEdgeV1(LayoutRoleV1.Item,
                                new ActualLayoutNodeV1(LayoutKindV1.Token, [1], [])),
                        ]),
                    null),
                new CorpusBoundsV1(0, 0, 30, 10)),
        ],
        null));

    public static RecognizeActualV1 RefusedActual() => new(
        new ActualPageV1(
        [
            new ActualRegionV1(
                "runtime-region-1",
                ["stroke-a", "stroke-b"],
                new RefusedRegionActualV1(
                    CorpusFailureCategoryV1.SpatialRelation,
                    CorpusRefusalCodeV1.SpatialAmbiguity),
                new CorpusBoundsV1(0, 0, 30, 10)),
        ],
        null));

    public static RecognizeActualV1 WrongLabelAndLatexActual(string latex = "7-") => new(
        new ActualPageV1(
        [
            new ActualRegionV1(
                "runtime-region-1",
                ["stroke-a", "stroke-b"],
                new AcceptedRegionActualV1(
                    latex,
                    [
                        new ActualTokenV1("7", ["stroke-a"], 0.99, false),
                        new ActualTokenV1("-", ["stroke-b"], 0.99, false),
                    ],
                    new ActualLayoutNodeV1(
                        LayoutKindV1.Sequence,
                        [],
                        [
                            new ActualLayoutEdgeV1(LayoutRoleV1.Item,
                                new ActualLayoutNodeV1(LayoutKindV1.Token, [0], [])),
                            new ActualLayoutEdgeV1(LayoutRoleV1.Item,
                                new ActualLayoutNodeV1(LayoutKindV1.Token, [1], [])),
                        ]),
                    null),
                new CorpusBoundsV1(0, 0, 30, 10)),
        ],
        null));
}
