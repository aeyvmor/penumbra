using System.Text.Json;
using System.Text.Json.Nodes;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class CorpusContractTests
{
    [Fact]
    public void Json_RoundTripPreservesStrokeAndSampleOrder()
    {
        ExpressionCaseV1 original = CorpusTestData.ValidCase();

        string json = CorpusJson.Serialize(original);
        ExpressionCaseV1 roundTrip = CorpusJson.DeserializeCase(json);

        Assert.Equal(original.Strokes.Select(stroke => stroke.StrokeId),
            roundTrip.Strokes.Select(stroke => stroke.StrokeId));
        Assert.Equal(original.Strokes[0].Samples, roundTrip.Strokes[0].Samples);
        Assert.Equal(original.Strokes[1].Samples, roundTrip.Strokes[1].Samples);
    }

    [Fact]
    public void Json_RejectsUnknownMembersAndIntegerEnums()
    {
        string json = CorpusJson.Serialize(CorpusTestData.ValidCase());

        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(
            json.Replace("\"caseRevision\": 1", "\"caseRevision\": 1, \"privateNote\": \"leak\"")));
        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(
            json.Replace("\"partition\": \"development\"", "\"partition\": 0")));
    }

    [Fact]
    public void Json_RejectsDuplicateMembers()
    {
        string json = CorpusJson.Serialize(CorpusTestData.ValidCase());

        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(
            json.Replace(
                "\"caseRevision\": 1",
                "\"caseRevision\": 1, \"caseRevision\": 1",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void Json_RejectsMissingRequiredSampleAndCaseMembers()
    {
        string json = CorpusJson.Serialize(CorpusTestData.ValidCase());
        JsonObject missingPressure = ParseObject(json);
        JsonArray strokes = Assert.IsType<JsonArray>(missingPressure["strokes"]);
        JsonObject firstStroke = Assert.IsType<JsonObject>(strokes[0]);
        JsonArray samples = Assert.IsType<JsonArray>(firstStroke["samples"]);
        Assert.True(Assert.IsType<JsonObject>(samples[0]).Remove("pressure"));

        JsonObject missingPartition = ParseObject(json);
        Assert.True(missingPartition.Remove("partition"));

        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(missingPressure.ToJsonString()));
        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(missingPartition.ToJsonString()));
    }

    [Fact]
    public void Json_RejectsMissingInheritedStepId()
    {
        JsonObject root = ParseObject(CorpusJson.Serialize(CorpusTestData.ValidCase()));
        JsonArray steps = Assert.IsType<JsonArray>(root["steps"]);
        Assert.True(Assert.IsType<JsonObject>(steps[0]).Remove("stepId"));

        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(root.ToJsonString()));
    }

    [Fact]
    public void Json_RejectsMissingManifestPartitionAndValidatorRejectsExplicitNullCollections()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        ExpressionCorpusSuite suite = CorpusTestData.Suite(
            (@case, CorpusCaseStatusV1.Development, null));
        JsonObject manifest = ParseObject(CorpusJson.Serialize(suite.Manifest));
        JsonArray entries = Assert.IsType<JsonArray>(manifest["entries"]);
        Assert.True(Assert.IsType<JsonObject>(entries[0]).Remove("partition"));
        Assert.Throws<JsonException>(() => CorpusJson.DeserializeManifest(manifest.ToJsonString()));

        JsonObject caseJson = ParseObject(CorpusJson.Serialize(@case));
        caseJson["steps"] = null;
        ExpressionCaseV1 explicitNull = CorpusJson.DeserializeCase(caseJson.ToJsonString());
        Assert.Contains(CorpusValidator.ValidateCase(explicitNull),
            error => error.Code == CorpusErrorCode.MissingValue
                && error.Location == "case.steps");
    }

    [Theory]
    [InlineData("basis")]
    [InlineData("privateRemoteStorageAllowed")]
    [InlineData("privateModelTrainingAllowed")]
    [InlineData("publicRedistributionAllowed")]
    [InlineData("recordedAtUtc")]
    public void Json_RejectsMissingRequiredConsentMembers(string propertyName)
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 privateCase = baseline with
        {
            Capture = baseline.Capture with
            {
                Source = CorpusCaptureSourceV1.UserRealPen,
                DataClassification = CorpusDataClassificationV1.PrivateOwnedInk,
                DeviceClass = CorpusDeviceClassV1.ActivePen,
                PressureMode = CorpusPressureModeV1.Normalized,
                CaptureApi = CorpusCaptureApiV1.AvaloniaPointer,
                Consent = CorpusTestData.ValidPrivateConsent(),
            },
        };
        JsonObject root = ParseObject(CorpusJson.Serialize(privateCase));
        JsonObject capture = Assert.IsType<JsonObject>(root["capture"]);
        JsonObject consent = Assert.IsType<JsonObject>(capture["consent"]);
        Assert.True(consent.Remove(propertyName));

        Assert.Throws<JsonException>(() => CorpusJson.DeserializeCase(root.ToJsonString()));
    }

    [Fact]
    public void Validator_RejectsFutureSchemaAndInvalidFormat()
    {
        ExpressionCaseV1 future = CorpusTestData.ValidCase() with { SchemaVersion = 2 };
        ExpressionCaseV1 wrongFormat = CorpusTestData.ValidCase() with { Format = "some-other-format" };

        Assert.Contains(CorpusValidator.ValidateCase(future), error => error.Code == CorpusErrorCode.UnsupportedSchemaVersion);
        Assert.Contains(CorpusValidator.ValidateCase(wrongFormat), error => error.Code == CorpusErrorCode.InvalidFormat);
    }

    private static JsonObject ParseObject(string json) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(json));

    [Fact]
    public void Validator_RejectsNullHostileMembersWithoutThrowing()
    {
        ExpressionCaseV1 hostile = CorpusTestData.ValidCase() with
        {
            Capture = null!,
            Strokes = [null!],
            InitialStrokeIds = [],
            Steps = [null!],
        };

        IReadOnlyList<CorpusValidationError> errors = CorpusValidator.ValidateCase(hostile);

        Assert.Contains(errors, error => error.Code == CorpusErrorCode.MissingValue);
    }

    [Fact]
    public void Validator_RejectsNestedExplicitNullsWithoutThrowing()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        RecognizeStepV1 recognize = CorpusTestData.AcceptedStep();
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        ExpressionCaseV1 nullRegion = baseline with
        {
            Steps =
            [
                recognize with
                {
                    Expected = recognize.Expected with
                    {
                        Regions = [region with { RegionKey = null!, StrokeIds = null! }],
                    },
                },
            ],
        };
        ExpressionCaseV1 nullStore = baseline with
        {
            Steps = [new SaveStepV1("save-null", null!, CorpusSaveModeV1.Explicit)],
        };

        IReadOnlyList<CorpusValidationError> regionErrors = CorpusValidator.ValidateCase(nullRegion);
        IReadOnlyList<CorpusValidationError> storeErrors = CorpusValidator.ValidateCase(nullStore);

        Assert.Contains(regionErrors, error => error.Code == CorpusErrorCode.MissingValue);
        Assert.Contains(storeErrors, error => error.Code == CorpusErrorCode.MissingValue);
    }

    [Fact]
    public void Validator_BoundsEvidenceMustMatchInkAndUseNonVacuousTolerance()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        RecognizeStepV1 recognize = CorpusTestData.AcceptedStep();
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        ExpressionCaseV1 hostile = baseline with
        {
            Steps =
            [
                recognize with
                {
                    Expected = recognize.Expected with
                    {
                        Regions =
                        [
                            region with
                            {
                                Bounds = new CorpusBoundsV1(100, 0, 30, 10),
                                BoundsTolerance = double.MaxValue,
                            },
                        ],
                    },
                },
            ],
        };

        Assert.Contains(CorpusValidator.ValidateCase(hostile),
            error => error.Code == CorpusErrorCode.InvalidExpectedOutcome
                && error.Location.EndsWith(".bounds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsUndefinedEnumsAndVacuousGraphAnchors()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 undefined = baseline with
        {
            Partition = (CorpusPartitionV1)999,
            Capture = baseline.Capture with { Source = (CorpusCaptureSourceV1)999 },
            Steps = [new SaveStepV1("save-undefined", "page", (CorpusSaveModeV1)999)],
        };
        ExpressionCaseV1 graph = baseline with
        {
            Steps =
            [
                CorpusTestData.AcceptedStep(),
                new GraphStepV1(
                    "graph-hostile",
                    "region-1",
                    -1,
                    1,
                    2,
                    CorpusGraphDecisionV1.Graph,
                    "x",
                    [
                        new ExpectedGraphSampleV1(0, 0, double.MaxValue),
                        new ExpectedGraphSampleV1(0, 1, 0.01),
                    ]),
            ],
        };

        CorpusErrorCode[] undefinedCodes = CorpusValidator.ValidateCase(undefined)
            .Select(error => error.Code)
            .ToArray();
        Assert.Contains(CorpusErrorCode.InvalidFormat, undefinedCodes);
        Assert.Contains(CorpusErrorCode.InvalidCaptureMetadata, undefinedCodes);
        Assert.Contains(CorpusValidator.ValidateCase(graph),
            error => error.Code == CorpusErrorCode.InvalidScenarioOrder);
    }

    [Fact]
    public void Validator_EnforcesTextEvaluationAndSheetRoleInvariants()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        RecognizeStepV1 recognize = CorpusTestData.AcceptedStep();
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        AcceptedRegionExpectationV1 accepted = Assert.IsType<AcceptedRegionExpectationV1>(region.Expectation);
        string maximumText = new('x', CorpusResourceLimitsV1.MaximumTextLength);
        ExpressionCaseV1 boundary = CorpusTestData.ReplaceExpectedRegion(
            baseline,
            region with
            {
                Expectation = accepted with
                {
                    Cas = new ExpectedEvaluationV1(
                        CorpusEvaluationKindV1.Error,
                        false,
                        maximumText),
                },
            });
        Assert.DoesNotContain(CorpusValidator.ValidateCase(boundary),
            error => error.Code == CorpusErrorCode.InvalidExpectedOutcome);

        ExpressionCaseV1 overlong = CorpusTestData.ReplaceExpectedRegion(
            baseline,
            region with
            {
                Expectation = accepted with
                {
                    Latex = new string('x', CorpusResourceLimitsV1.MaximumTextLength + 1),
                    Cas = new ExpectedEvaluationV1(
                        CorpusEvaluationKindV1.Pending,
                        true,
                        "pending"),
                },
            });
        ExpressionCaseV1 roleMismatch = baseline with
        {
            Steps =
            [
                recognize with
                {
                    Expected = recognize.Expected with
                    {
                        Sheet = new ExpectedSheetV1(
                            [
                                new ExpectedSheetNodeV1(
                                    "region-1",
                                    CorpusSheetRoleV1.Query,
                                    "x",
                                    [],
                                    false,
                                    null),
                            ],
                            [],
                            []),
                    },
                },
            ],
        };

        Assert.Contains(CorpusValidator.ValidateCase(overlong),
            error => error.Code == CorpusErrorCode.InvalidExpectedOutcome);
        Assert.Contains(CorpusValidator.ValidateCase(roleMismatch),
            error => error.Code == CorpusErrorCode.InvalidSheetExpectation);
    }

    [Fact]
    public void Validator_BoundsWorkAndCapsErrorVolumeOnHostileInMemoryCases()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        CorpusStepV1[] tooManySteps = Enumerable.Range(
                0,
                CorpusResourceLimitsV1.MaximumStepsPerCase + 1)
            .Select(index => (CorpusStepV1)new UndoStepV1($"undo-{index}"))
            .ToArray();
        ExpressionCaseV1 oversized = baseline with { Steps = tooManySteps };

        IReadOnlyList<CorpusValidationError> resourceErrors = CorpusValidator.ValidateCase(oversized);
        Assert.Contains(resourceErrors, error => error.Code == CorpusErrorCode.ResourceLimitExceeded);

        CorpusSampleV1[] hostileSamples = Enumerable.Range(0, 6_000)
            .Select(index => new CorpusSampleV1(double.NaN, double.NaN, index, 2))
            .ToArray();
        ExpressionCaseV1 noisy = baseline with
        {
            Strokes =
            [
                baseline.Strokes[0] with { Samples = hostileSamples },
                baseline.Strokes[1],
            ],
        };
        Assert.Equal(
            CorpusResourceLimitsV1.MaximumValidationErrors,
            CorpusValidator.ValidateCase(noisy).Count);
    }

    [Fact]
    public void Validator_CapsCumulativeScenarioStateSnapshots()
    {
        CorpusStrokeV1[] strokes = Enumerable.Range(0, 1_000)
            .Select(index => new CorpusStrokeV1(
                $"stroke-{index:D4}",
                null,
                [new CorpusSampleV1(index, 0, 0, 0.5)]))
            .ToArray();
        SaveStepV1[] saves = Enumerable.Range(0, 600)
            .Select(index => new SaveStepV1(
                $"save-{index:D4}",
                "page",
                CorpusSaveModeV1.Autosave))
            .ToArray();
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Strokes = strokes,
            InitialStrokeIds = strokes.Select(stroke => stroke.StrokeId).ToArray(),
            Steps = saves,
        };

        Assert.Contains(CorpusValidator.ValidateCase(@case),
            error => error.Code == CorpusErrorCode.ResourceLimitExceeded);
    }

    [Fact]
    public void Validator_RejectsNonFiniteCoordinatesDecreasingTicksAndInvalidPressure()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        @case = @case with
        {
            Strokes =
            [
                @case.Strokes[0] with
                {
                    Samples =
                    [
                        new CorpusSampleV1(double.NaN, 0, 10, 1.1),
                        new CorpusSampleV1(1, 1, 9, 0.5),
                    ],
                },
                @case.Strokes[1],
            ],
        };

        CorpusErrorCode[] codes = CorpusValidator.ValidateCase(@case).Select(error => error.Code).ToArray();

        Assert.Contains(CorpusErrorCode.NonFiniteNumber, codes);
        Assert.Contains(CorpusErrorCode.NonMonotonicTime, codes);
        Assert.Contains(CorpusErrorCode.InvalidPressure, codes);
    }

    [Fact]
    public void AcceptedRegion_RequiresTokenSourcesToPartitionRegionExactly()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        RecognizeStepV1 recognize = Assert.IsType<RecognizeStepV1>(@case.Steps[^1]);
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        AcceptedRegionExpectationV1 accepted = Assert.IsType<AcceptedRegionExpectationV1>(region.Expectation);
        accepted = accepted with
        {
            Tokens =
            [
                accepted.Tokens[0],
                accepted.Tokens[1] with { SourceStrokeIds = ["stroke-a"] },
            ],
        };
        @case = CorpusTestData.ReplaceExpectedRegion(@case, region with { Expectation = accepted });

        CorpusErrorCode[] codes = CorpusValidator.ValidateCase(@case).Select(error => error.Code).ToArray();

        Assert.Contains(CorpusErrorCode.DuplicateStrokeOwnership, codes);
        Assert.Contains(CorpusErrorCode.UnownedStroke, codes);
    }

    [Fact]
    public void AcceptedLayout_OwnsEveryTokenExactlyOnceRecursively()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        RecognizeStepV1 recognize = Assert.IsType<RecognizeStepV1>(@case.Steps[^1]);
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        AcceptedRegionExpectationV1 accepted = Assert.IsType<AcceptedRegionExpectationV1>(region.Expectation);
        ExpectedLayoutNodeV1 badLayout = accepted.Layout with
        {
            Children =
            [
                accepted.Layout.Children[0],
                accepted.Layout.Children[0],
            ],
        };
        @case = CorpusTestData.ReplaceExpectedRegion(
            @case, region with { Expectation = accepted with { Layout = badLayout } });

        CorpusErrorCode[] codes = CorpusValidator.ValidateCase(@case).Select(error => error.Code).ToArray();

        Assert.Contains(CorpusErrorCode.DuplicateTokenOwnership, codes);
        Assert.Contains(CorpusErrorCode.UnownedToken, codes);
    }

    [Fact]
    public void LayoutValidator_RejectsReferenceCyclesWithoutRecursingForever()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        RecognizeStepV1 recognize = CorpusTestData.AcceptedStep();
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        AcceptedRegionExpectationV1 accepted = Assert.IsType<AcceptedRegionExpectationV1>(region.Expectation);
        var children = new List<ExpectedLayoutEdgeV1>();
        var cycle = new ExpectedLayoutNodeV1(LayoutKindV1.Sequence, [], children);
        children.Add(new ExpectedLayoutEdgeV1(LayoutRoleV1.Item, cycle));
        @case = CorpusTestData.ReplaceExpectedRegion(
            @case,
            region with { Expectation = accepted with { Layout = cycle } });

        Assert.Contains(CorpusValidator.ValidateCase(@case),
            error => error.Code == CorpusErrorCode.ResourceLimitExceeded);
    }

    [Fact]
    public void LayoutKinds_EnforceRequiredChildRoles()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidFractionCase();
        Assert.DoesNotContain(CorpusValidator.ValidateCase(@case), error => error.Code == CorpusErrorCode.InvalidLayoutShape);

        RecognizeStepV1 recognize = Assert.IsType<RecognizeStepV1>(@case.Steps[^1]);
        ExpectedRegionV1 region = recognize.Expected.Regions[0];
        AcceptedRegionExpectationV1 accepted = Assert.IsType<AcceptedRegionExpectationV1>(region.Expectation);
        ExpectedLayoutNodeV1 missingDenominator = accepted.Layout with
        {
            Children = accepted.Layout.Children.Where(edge => edge.Role != LayoutRoleV1.Denominator).ToArray(),
        };
        @case = CorpusTestData.ReplaceExpectedRegion(
            @case, region with { Expectation = accepted with { Layout = missingDenominator } });

        Assert.Contains(CorpusValidator.ValidateCase(@case), error => error.Code == CorpusErrorCode.InvalidLayoutShape);
    }

    [Fact]
    public void Scenario_RejectsDeadStrokeReferencesAndUnsafeStoreSlots()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Steps =
            [
                new EraseStepV1("erase-1", ["missing-stroke"]),
                new SaveStepV1("save-1", "..\\private.pen", CorpusSaveModeV1.Explicit),
            ],
        };

        CorpusErrorCode[] codes = CorpusValidator.ValidateCase(@case).Select(error => error.Code).ToArray();

        Assert.Contains(CorpusErrorCode.DeadStrokeReference, codes);
        Assert.Contains(CorpusErrorCode.UnsafeLogicalId, codes);
    }

    [Fact]
    public void Scenario_ReopenRestoresTheSavedLogicalStrokeSnapshot()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        RecognizeStepV1 expectedAfterReopen = CorpusTestData.AcceptedStep();
        ExpectedRegionV1 onlyStrokeA = expectedAfterReopen.Expected.Regions[0] with
        {
            StrokeIds = ["stroke-a"],
            Bounds = new CorpusBoundsV1(0, 0, 10, 10),
            Expectation = new AcceptedRegionExpectationV1(
                "1",
                [new ExpectedTokenV1("token-a", "1", ["stroke-a"])],
                new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["token-a"], []),
                null),
        };
        ExpressionCaseV1 @case = baseline with
        {
            InitialStrokeIds = ["stroke-a"],
            Steps =
            [
                new SaveStepV1("save-1", "page", CorpusSaveModeV1.Explicit),
                new RewriteStepV1("rewrite-1", ["stroke-a"], ["stroke-b"]),
                new ReopenStepV1(
                    "reopen-1",
                    "page",
                    CorpusOpenStatusV1.OpenedCurrent,
                    new ExpectedPageV1([onlyStrokeA], null)),
            ],
        };

        Assert.Empty(CorpusValidator.ValidateCase(@case));
    }

    [Fact]
    public void Scenario_RecoveryRequiresARealLkgAndRestoresThePriorSnapshot()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpectedPageV1 pageA = ExpectedSingleStrokePage("stroke-a", "1");
        ExpressionCaseV1 noLkg = baseline with
        {
            InitialStrokeIds = ["stroke-a"],
            Steps =
            [
                new SaveStepV1("save-1", "page", CorpusSaveModeV1.Explicit),
                new RecoverStepV1(
                    "recover-1",
                    "page",
                    CorpusRecoveryDamageV1.CorruptCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    pageA),
                new AddInkStepV1("add-1", ["stroke-b"]),
            ],
        };

        Assert.Contains(CorpusValidator.ValidateCase(noLkg),
            error => error.Code == CorpusErrorCode.InvalidScenarioOrder);

        ExpressionCaseV1 withLkg = baseline with
        {
            InitialStrokeIds = ["stroke-a"],
            Steps =
            [
                new SaveStepV1("save-a", "page", CorpusSaveModeV1.Explicit),
                new RewriteStepV1("rewrite-b", ["stroke-a"], ["stroke-b"]),
                new SaveStepV1("save-b", "page", CorpusSaveModeV1.Explicit),
                new RecoverStepV1(
                    "recover-a",
                    "page",
                    CorpusRecoveryDamageV1.CorruptCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    pageA),
            ],
        };

        Assert.Empty(CorpusValidator.ValidateCase(withLkg));
    }

    [Fact]
    public void Scenario_ValidCurrentWinsAStaleTemporaryCandidate()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 @case = baseline with
        {
            InitialStrokeIds = ["stroke-a"],
            Steps =
            [
                new SaveStepV1("save-1", "page", CorpusSaveModeV1.Explicit),
                new RecoverStepV1(
                    "recover-current",
                    "page",
                    CorpusRecoveryDamageV1.StaleTemporaryCandidate,
                    CorpusOpenStatusV1.OpenedCurrent,
                    ExpectedSingleStrokePage("stroke-a", "1")),
                new AddInkStepV1("add-unused", ["stroke-b"]),
            ],
        };

        Assert.Empty(CorpusValidator.ValidateCase(@case));
    }

    [Fact]
    public void StampOutputStrokeCountIsIndependentOfSourceExpressionStrokeCount()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 @case = baseline with
        {
            Steps =
            [
                CorpusTestData.AcceptedStep(),
                new StampStepV1(
                    "stamp-1",
                    "region-1",
                    new CorpusPointV1(100, 50),
                    new CorpusPointV1(130, 60),
                    CorpusStampDecisionV1.Append,
                    1,
                    [],
                    [
                        SyntheticStampStroke("synth-a", 100),
                        SyntheticStampStroke("synth-b", 110),
                        SyntheticStampStroke("synth-c", 120),
                    ]),
            ],
        };

        Assert.Empty(CorpusValidator.ValidateCase(@case));
    }

    [Theory]
    [InlineData(0.099)]
    [InlineData(20.001)]
    public void Scenario_TaffyRejectsCanvasScaleOutsideTheProductZoomRange(double canvasScale)
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 @case = baseline with
        {
            Steps =
            [
                CorpusTestData.AcceptedStep(),
                new TaffyProbeStepV1(
                    "taffy-impossible-scale",
                    "region-1",
                    [new LayoutPathSegmentV1(LayoutRoleV1.Item, 0)],
                    ["stroke-a"],
                    new CorpusPointV1(5, 5),
                    CumulativeScreenDeltaX: 14,
                    CanvasScale: canvasScale,
                    TrialLatex: "2+",
                    ExpectedSheet: new ExpectedSheetV1([], [], [])),
            ],
        };

        Assert.Contains(
            CorpusValidator.ValidateCase(@case),
            error => error.Code == CorpusErrorCode.InvalidScenarioOrder);
    }

    [Fact]
    public void MutationInvalidatesStaleRegionTargets()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 @case = baseline with
        {
            Steps =
            [
                CorpusTestData.AcceptedStep(),
                new EraseStepV1("erase-1", ["stroke-b"]),
                new StampStepV1(
                    "stamp-stale",
                    "region-1",
                    new CorpusPointV1(100, 50),
                    new CorpusPointV1(130, 60),
                    CorpusStampDecisionV1.Append,
                    1,
                    [],
                    [SyntheticStampStroke("synth-a", 100)]),
            ],
        };

        Assert.Contains(CorpusValidator.ValidateCase(@case),
            error => error.Code == CorpusErrorCode.InvalidScenarioOrder);
    }

    [Fact]
    public void RealPenCase_RequiresDenyByDefaultScopedConsent()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Capture = CorpusTestData.ValidCase().Capture with
            {
                Source = CorpusCaptureSourceV1.UserRealPen,
                DataClassification = CorpusDataClassificationV1.PrivateOwnedInk,
                Consent = null,
            },
        };

        Assert.Contains(CorpusValidator.ValidateCase(@case), error => error.Code == CorpusErrorCode.InvalidConsent);

        @case = @case with
        {
            Capture = @case.Capture with
            {
                Consent = CorpusTestData.ValidPrivateConsent() with { PublicRedistributionAllowed = true },
            },
        };
        Assert.Contains(CorpusValidator.ValidateCase(@case), error => error.Code == CorpusErrorCode.InvalidConsent);
    }

    [Fact]
    public void RefusalReasonMustBelongToItsReportedFirstStage()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            Steps =
            [
                CorpusTestData.ExpectedRefusalStep() with
                {
                    Expected = CorpusTestData.ExpectedRefusalStep().Expected with
                    {
                        Regions =
                        [
                            CorpusTestData.ExpectedRefusalStep().Expected.Regions[0] with
                            {
                                Expectation = new RefusedRegionExpectationV1(
                                    CorpusFailureCategoryV1.Cas,
                                    CorpusRefusalCodeV1.LowConfidence),
                            },
                        ],
                    },
                },
            ],
        };

        Assert.Contains(
            CorpusValidator.ValidateCase(@case),
            error => error.Code == CorpusErrorCode.InvalidExpectedOutcome);
    }

    [Fact]
    public void RealPenCase_AcceptsTheExplicitPrivateOwnedCaptureProfile()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 @case = baseline with
        {
            Strokes =
            [
                baseline.Strokes[0] with { StartOffsetTicks = 0 },
                baseline.Strokes[1] with { StartOffsetTicks = 20 },
            ],
            Capture = baseline.Capture with
            {
                Source = CorpusCaptureSourceV1.UserRealPen,
                DataClassification = CorpusDataClassificationV1.PrivateOwnedInk,
                DeviceClass = CorpusDeviceClassV1.ActivePen,
                PressureMode = CorpusPressureModeV1.Normalized,
                CaptureApi = CorpusCaptureApiV1.AvaloniaPointer,
                Consent = CorpusTestData.ValidPrivateConsent(),
            },
        };

        CorpusErrorCode[] codes = CorpusValidator.ValidateCase(@case)
            .Select(error => error.Code)
            .ToArray();
        Assert.DoesNotContain(CorpusErrorCode.InvalidCaptureMetadata, codes);
        Assert.DoesNotContain(CorpusErrorCode.InvalidConsent, codes);
    }

    [Fact]
    public void RealPenCase_RequiresOrderedInterStrokeTiming()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        ExpressionCaseV1 @case = baseline with
        {
            Strokes =
            [
                baseline.Strokes[0] with { StartOffsetTicks = 20 },
                baseline.Strokes[1] with { StartOffsetTicks = 10 },
            ],
            Capture = baseline.Capture with
            {
                Source = CorpusCaptureSourceV1.UserRealPen,
                DataClassification = CorpusDataClassificationV1.PrivateOwnedInk,
                DeviceClass = CorpusDeviceClassV1.ActivePen,
                PressureMode = CorpusPressureModeV1.Normalized,
                CaptureApi = CorpusCaptureApiV1.AvaloniaPointer,
                Consent = CorpusTestData.ValidPrivateConsent(),
            },
        };

        Assert.Contains(
            CorpusValidator.ValidateCase(@case),
            error => error.Code == CorpusErrorCode.NonMonotonicTime
                && error.Location.EndsWith("startOffsetTicks", StringComparison.Ordinal));
    }

    private static CorpusStrokeV1 SyntheticStampStroke(string strokeId, double x) => new(
        strokeId,
        null,
        [
            new CorpusSampleV1(x, 50, 0, 0.5),
            new CorpusSampleV1(x + 5, 55, 10, 0.5),
        ]);

    private static ExpectedPageV1 ExpectedSingleStrokePage(string strokeId, string latex) => new(
        [
            new ExpectedRegionV1(
                "region-single",
                [strokeId],
                new CorpusBoundsV1(0, 0, 10, 10),
                0.01,
                new AcceptedRegionExpectationV1(
                    latex,
                    [new ExpectedTokenV1("token-single", latex, [strokeId])],
                    new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["token-single"], []),
                    null)),
        ],
        null);
}
