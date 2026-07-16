using Penumbra.Core;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class RunnerReadinessProfileTests
{
    [Fact]
    public async Task TwoSheetRowsWithoutACrossRegionDependencyDoNotSatisfyMultiLineCoverage()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase("dev-independent-lines-001");
        var page = new ExpectedPageV1(
        [
            ExpectedTokenRegion("region-a", "stroke-a", new CorpusBoundsV1(0, 0, 10, 10), "1"),
            ExpectedTokenRegion("region-b", "stroke-b", new CorpusBoundsV1(20, 0, 10, 10), "2"),
        ],
        new ExpectedSheetV1(
        [
            new ExpectedSheetNodeV1(
                "region-a", CorpusSheetRoleV1.Statement, null, [], false, Number("1")),
            new ExpectedSheetNodeV1(
                "region-b", CorpusSheetRoleV1.Statement, null, [], false, Number("2")),
        ],
        ["region-a", "region-b"],
        ["region-a", "region-b"]));
        ExpressionCaseV1 @case = baseline with
        {
            Steps = [new RecognizeStepV1("recognize-independent-lines", page)],
        };
        var actual = new RecognizeActualV1(new ActualPageV1(
        [
            ActualTokenRegion("runtime-a", "stroke-a", new CorpusBoundsV1(0, 0, 10, 10), "1"),
            ActualTokenRegion("runtime-b", "stroke-b", new CorpusBoundsV1(20, 0, 10, 10), "2"),
        ],
        new ActualSheetV1(
        [
            new ActualSheetNodeV1(
                "runtime-a", ["stroke-a"], CorpusSheetRoleV1.Statement, null, [], false, Number("1")),
            new ActualSheetNodeV1(
                "runtime-b", ["stroke-b"], CorpusSheetRoleV1.Statement, null, [], false, Number("2")),
        ],
        ["runtime-a", "runtime-b"],
        ["runtime-a", "runtime-b"])));

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new ReadinessRuntimeFactory([actual]),
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                MetricsCapacity: 64,
                RequireMetricCoverage: false),
            default);

        Assert.True(report.ProfilePassed);
        Assert.Equal(0, report.Coverage[CorpusCoverageFeatureV1.MultiLineDependency]);
    }

    [Fact]
    public async Task Slice3ReadinessCanPassOnlyWithEveryScenarioAndBoundedMetricCovered()
    {
        ExpressionCaseV1 @case = ReadinessCase();
        IReadOnlyList<CorpusValidationError> validation = CorpusValidator.ValidateCase(@case);
        Assert.Empty(validation);

        var factory = new ReadinessRuntimeFactory(
        [
            CorpusTestData.ExactActual(),
            CorpusTestData.RefusedActual(),
            Mutation("stroke-a"),
            Mutation("stroke-a", "stroke-b"),
            Mutation("stroke-a"),
            Mutation("stroke-a", "stroke-b"),
            Mutation("stroke-a", "stroke-c"),
            MultiLineActual(),
            StampActual(),
            PostStampActual("runtime-post"),
            new TaffyProbeActualV1("223", PostStampSheet("runtime-post")),
            new PersistenceWriteActualV1(true),
            new PersistenceWriteActualV1(true),
            new PersistenceOpenActualV1(
                CorpusOpenStatusV1.OpenedCurrent,
                StateWithStamp(),
                PostStampActual("runtime-reopen").Actual),
            new PersistenceOpenActualV1(
                CorpusOpenStatusV1.BackupRecoveryCandidate,
                StateWithStamp(),
                PostStampActual("runtime-recover").Actual),
            new GraphActualV1(
                CorpusGraphDecisionV1.Graph,
                "x",
                [new ActualGraphSampleV1(0, 0), new ActualGraphSampleV1(1, 1)]),
        ]);

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            factory,
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                MetricsCapacity: 256,
                Profile: CorpusRunProfileV1.Slice3DevelopmentReadiness),
            default);

        Assert.True(report.InfrastructureValid);
        Assert.True(report.AccuracyPassed);
        Assert.True(report.CoveragePassed);
        Assert.True(report.LatencyPassed);
        Assert.True(report.ProfilePassed);
        Assert.True(report.ReadinessPassed);
        Assert.Equal(0, report.AcceptedWrongCount);
        Assert.Equal(0, report.MissingMetricOperationCount);
        Assert.Equal(0, report.MissingCoverageFeatureCount);
        Assert.Equal(0, report.LatencyBudgetViolationCount);
        Assert.All(report.Coverage, pair => Assert.True(pair.Value > 0, pair.Key.ToString()));
        Assert.Equal("runtime-recover", factory.GraphSourceRegionHandle);
    }

    private static ExpressionCaseV1 ReadinessCase()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase(
            "dev-readiness-complete-001",
            "session-readiness-001");
        CorpusStrokeV1 strokeA = baseline.Strokes[0] with { StartOffsetTicks = 0 };
        CorpusStrokeV1 strokeB = baseline.Strokes[1] with { StartOffsetTicks = 20 };
        var strokeC = new CorpusStrokeV1(
            "stroke-c",
            40,
            [
                new CorpusSampleV1(0, 20, 0, 0.5),
                new CorpusSampleV1(10, 30, 10, 0.6),
            ]);
        RecognizeStepV1 accepted = CorpusTestData.AcceptedStep();
        RecognizeStepV1 refused = CorpusTestData.ExpectedRefusalStep() with
        {
            StepId = "recognize-refusal",
        };
        return baseline with
        {
            Capture = baseline.Capture with
            {
                Source = CorpusCaptureSourceV1.UserRealPen,
                DataClassification = CorpusDataClassificationV1.PrivateOwnedInk,
                WriterId = "owned-writer-001",
                DeviceClass = CorpusDeviceClassV1.ActivePen,
                PressureMode = CorpusPressureModeV1.Normalized,
                CaptureApi = CorpusCaptureApiV1.AvaloniaPointer,
                CaptureBuild = "readiness-v1",
                Consent = CorpusTestData.ValidPrivateConsent(),
            },
            Strokes = [strokeA, strokeB, strokeC],
            Steps =
            [
                accepted,
                refused,
                new EraseStepV1("erase-b", ["stroke-b"]),
                new UndoStepV1("undo-erase"),
                new RedoStepV1("redo-erase"),
                new UndoStepV1("restore-b"),
                new RewriteStepV1("rewrite-b-as-c", ["stroke-b"], ["stroke-c"]),
                MultiLineStep(),
                new StampStepV1(
                    "stamp-answer",
                    "region-a",
                    new CorpusPointV1(40, 0),
                    new CorpusPointV1(45, 5),
                    CorpusStampDecisionV1.Append,
                    1,
                    [],
                    [StampStroke("stamp-alias")]),
                PostStampStep("recognize-post-stamp"),
                new TaffyProbeStepV1(
                    "taffy-post-stamp",
                    "region-all",
                    [new LayoutPathSegmentV1(LayoutRoleV1.Item, 0)],
                    ["stroke-a"],
                    new CorpusPointV1(5, 5),
                    10,
                    1,
                    "223",
                    PostStampExpectedSheet()),
                new SaveStepV1("autosave", "page-slot", CorpusSaveModeV1.Autosave),
                new SaveStepV1("explicit-save", "page-slot", CorpusSaveModeV1.Explicit),
                new ReopenStepV1(
                    "reopen",
                    "page-slot",
                    CorpusOpenStatusV1.OpenedCurrent,
                    PostStampPage()),
                new RecoverStepV1(
                    "recover",
                    "page-slot",
                    CorpusRecoveryDamageV1.CorruptCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    PostStampPage()),
                new GraphStepV1(
                    "graph",
                    "region-all",
                    0,
                    1,
                    2,
                    CorpusGraphDecisionV1.Graph,
                    "x",
                    [
                        new ExpectedGraphSampleV1(0, 0, 0.01),
                        new ExpectedGraphSampleV1(1, 1, 0.01),
                    ]),
            ],
        };
    }

    private static RecognizeStepV1 MultiLineStep() => new(
        "recognize-multi-line",
        new ExpectedPageV1(
        [
            ExpectedTokenRegion("region-a", "stroke-a", new CorpusBoundsV1(0, 0, 10, 10), "1"),
            ExpectedTokenRegion("region-c", "stroke-c", new CorpusBoundsV1(0, 20, 10, 10), "2"),
        ],
        new ExpectedSheetV1(
        [
            new ExpectedSheetNodeV1(
                "region-a",
                CorpusSheetRoleV1.Definition,
                "a",
                [],
                false,
                Number("1")),
            new ExpectedSheetNodeV1(
                "region-c",
                CorpusSheetRoleV1.Statement,
                null,
                ["a"],
                false,
                Number("2")),
        ],
        ["region-a", "region-c"],
        ["region-a", "region-c"])));

    private static RecognizeActualV1 MultiLineActual() => new(
        new ActualPageV1(
        [
            ActualTokenRegion("runtime-a", "stroke-a", new CorpusBoundsV1(0, 0, 10, 10), "1"),
            ActualTokenRegion("runtime-c", "stroke-c", new CorpusBoundsV1(0, 20, 10, 10), "2"),
        ],
        new ActualSheetV1(
        [
            new ActualSheetNodeV1(
                "runtime-a",
                ["stroke-a"],
                CorpusSheetRoleV1.Definition,
                "a",
                [],
                false,
                Number("1")),
            new ActualSheetNodeV1(
                "runtime-c",
                ["stroke-c"],
                CorpusSheetRoleV1.Statement,
                null,
                ["a"],
                false,
                Number("2")),
        ],
        ["runtime-a", "runtime-c"],
        ["runtime-a", "runtime-c"])));

    private static RecognizeStepV1 PostStampStep(string stepId) => new(stepId, PostStampPage());

    private static ExpectedPageV1 PostStampPage() => new(
    [
        new ExpectedRegionV1(
            "region-all",
            ["stroke-a", "stroke-c", "stamp-alias"],
            new CorpusBoundsV1(0, 0, 50, 30),
            0.01,
            new AcceptedRegionExpectationV1(
                "123",
                [
                    new ExpectedTokenV1("token-a", "1", ["stroke-a"]),
                    new ExpectedTokenV1("token-c", "2", ["stroke-c"]),
                    new ExpectedTokenV1("token-stamp", "3", ["stamp-alias"]),
                ],
                ExpectedSequence("token-a", "token-c", "token-stamp"),
                Number("123"))),
    ],
    PostStampExpectedSheet());

    private static RecognizeActualV1 PostStampActual(string runtimeHandle) => new(
        new ActualPageV1(
        [
            new ActualRegionV1(
                runtimeHandle,
                ["stroke-a", "stroke-c", "runtime-stamp"],
                new AcceptedRegionActualV1(
                    "123",
                    [
                        new ActualTokenV1("1", ["stroke-a"], 0.99, false),
                        new ActualTokenV1("2", ["stroke-c"], 0.99, false),
                        new ActualTokenV1("3", ["runtime-stamp"], 0.99, false),
                    ],
                    ActualSequence(0, 1, 2),
                    Number("123")),
                new CorpusBoundsV1(0, 0, 50, 30)),
        ],
        PostStampSheet(runtimeHandle)));

    private static ExpectedSheetV1 PostStampExpectedSheet() => new(
    [
        new ExpectedSheetNodeV1(
            "region-all",
            CorpusSheetRoleV1.Statement,
            null,
            [],
            false,
            Number("123")),
    ],
    ["region-all"],
    ["region-all"]);

    private static ActualSheetV1 PostStampSheet(string runtimeHandle) => new(
    [
        new ActualSheetNodeV1(
            runtimeHandle,
            ["stroke-a", "stroke-c", "runtime-stamp"],
            CorpusSheetRoleV1.Statement,
            null,
            [],
            false,
            Number("123")),
    ],
    [runtimeHandle],
    [runtimeHandle]);

    private static ExpectedRegionV1 ExpectedTokenRegion(
        string regionKey,
        string strokeId,
        CorpusBoundsV1 bounds,
        string latex) => new(
        regionKey,
        [strokeId],
        bounds,
        0.01,
        new AcceptedRegionExpectationV1(
            latex,
            [new ExpectedTokenV1($"token-{regionKey}", latex, [strokeId])],
            new ExpectedLayoutNodeV1(LayoutKindV1.Token, [$"token-{regionKey}"], []),
            Number(latex)));

    private static ActualRegionV1 ActualTokenRegion(
        string runtimeHandle,
        string strokeId,
        CorpusBoundsV1 bounds,
        string latex) => new(
        runtimeHandle,
        [strokeId],
        new AcceptedRegionActualV1(
            latex,
            [new ActualTokenV1(latex, [strokeId], 0.99, false)],
            new ActualLayoutNodeV1(LayoutKindV1.Token, [0], []),
            Number(latex)),
        bounds);

    private static ExpectedLayoutNodeV1 ExpectedSequence(params string[] tokenIds) => new(
        LayoutKindV1.Sequence,
        [],
        tokenIds.Select(tokenId => new ExpectedLayoutEdgeV1(
            LayoutRoleV1.Item,
            new ExpectedLayoutNodeV1(LayoutKindV1.Token, [tokenId], []))).ToArray());

    private static ActualLayoutNodeV1 ActualSequence(params int[] tokenIndexes) => new(
        LayoutKindV1.Sequence,
        [],
        tokenIndexes.Select(tokenIndex => new ActualLayoutEdgeV1(
            LayoutRoleV1.Item,
            new ActualLayoutNodeV1(LayoutKindV1.Token, [tokenIndex], []))).ToArray());

    private static StampActualV1 StampActual() => new(
        CorpusStampDecisionV1.Append,
        1,
        [new CorpusStrokeV1(
            "materialized-source",
            0,
            [
                new CorpusSampleV1(0, 0, 0, 0.5),
                new CorpusSampleV1(10, 10, 10, 0.6),
            ])],
        [],
        [StampStroke("runtime-stamp")],
        StateWithStamp());

    private static CorpusStrokeV1 StampStroke(string strokeId) => new(
        strokeId,
        0,
        [
            new CorpusSampleV1(40, 0, 0, 0.5),
            new CorpusSampleV1(50, 10, 10, 0.6),
        ]);

    private static MutationActualV1 Mutation(params string[] liveStrokeIds) => new(
        new ActualDocumentStateV1(liveStrokeIds, liveStrokeIds, []));

    private static ActualDocumentStateV1 StateWithStamp() => new(
        ["stroke-a", "stroke-c", "runtime-stamp"],
        ["stroke-a", "stroke-c"],
        ["runtime-stamp"]);

    private static ExpectedEvaluationV1 Number(string value) => new(
        CorpusEvaluationKindV1.Number,
        true,
        value);

    private sealed class ReadinessRuntimeFactory(IReadOnlyList<StepActualV1> results)
        : IExpressionScenarioRuntimeFactory
    {
        public string PipelineFingerprint => "readiness-contract-v1";

        public string ModelFingerprint => new('7', 64);

        public double RecognitionThreshold => 0.75;

        public string? GraphSourceRegionHandle { get; private set; }

        public IExpressionScenarioRuntime Create(ExpressionScenarioInputV1 input, ILocalMetricsSink metrics) =>
            new ReadinessRuntime(results, metrics, handle => GraphSourceRegionHandle = handle);
    }

    private sealed class ReadinessRuntime(
        IEnumerable<StepActualV1> results,
        ILocalMetricsSink metrics,
        Action<string> observeGraphHandle) : IExpressionScenarioRuntime
    {
        private readonly Queue<StepActualV1> _results = new(results);

        public Task<StepActualV1> ApplyAsync(ScenarioActionV1 action, CancellationToken cancellationToken)
        {
            RecordMetrics(action);
            if (action is GraphActionV1 graph)
            {
                observeGraphHandle(graph.SourceRegionHandle);
            }
            return Task.FromResult(_results.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void RecordMetrics(ScenarioActionV1 action)
        {
            void Record(params MetricOperation[] operations)
            {
                foreach (MetricOperation operation in operations)
                {
                    metrics.Record(new MetricObservation(
                        operation,
                        MetricOutcome.Completed,
                        TimeSpan.FromMilliseconds(1),
                        1));
                }
            }

            switch (action)
            {
                case RecognizeActionV1:
                    RecordRecognition();
                    break;
                case SaveActionV1 { Mode: CorpusSaveModeV1.Autosave }:
                    Record(MetricOperation.Autosave);
                    break;
                case SaveActionV1:
                    Record(MetricOperation.ExplicitSave);
                    break;
                case ReopenActionV1 or RecoverActionV1:
                    Record(MetricOperation.RecoveryRead);
                    RecordRecognition();
                    break;
                case TaffyProbeActionV1:
                    Record(
                        MetricOperation.TaffyProcessing,
                        MetricOperation.TaffyProbe,
                        MetricOperation.TaffyGhostSynthesis,
                        MetricOperation.TaffyPublication);
                    break;
                case GraphActionV1:
                    Record(MetricOperation.GraphDetection, MetricOperation.GraphSampling);
                    break;
                case CloseFlushActionV1:
                    Record(MetricOperation.CloseFlush);
                    break;
            }

            void RecordRecognition() => Record(
                MetricOperation.RecognitionProcessing,
                MetricOperation.RecognitionPartition,
                MetricOperation.RecognitionClassification,
                MetricOperation.RecognitionGrammar,
                MetricOperation.SheetRecompute);
        }
    }
}
