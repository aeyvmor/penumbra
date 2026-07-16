using Penumbra.Core;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class RunnerStampContractTests
{
    [Fact]
    public async Task StampReceivesOnlyRuntimeOwnedIntentAndProvesFreshTransformedGeometry()
    {
        ExpressionCaseV1 @case = StampCase();
        var factory = new RecordingSequenceFactory(
            CorpusTestData.ExactActual(),
            ExactStampActual(),
            PostStampActual());

        CorpusRunReport report = await RunAsync(@case, factory);

        Assert.True(report.ProfilePassed);
        Assert.Equal(0, report.StructuralMismatchCount);
        StampActionV1 action = Assert.IsType<StampActionV1>(factory.Actions[1]);
        Assert.Equal("runtime-region-1", action.SourceRegionHandle);
        Assert.Equal(new CorpusPointV1(100, 50), action.GestureDelta);
        Assert.Equal(new CorpusPointV1(130, 60), action.DropPoint);
    }

    [Fact]
    public async Task StampCannotPassByReturningIdsWithoutTheExpectedGeometry()
    {
        ExpressionCaseV1 @case = StampCase();
        StampActualV1 exact = ExactStampActual();
        CorpusStrokeV1 added = exact.AddedStrokes[0];
        StampActualV1 wrong = exact with
        {
            AddedStrokes =
            [
                added with
                {
                    Samples =
                    [
                        added.Samples[0] with { X = added.Samples[0].X + 1 },
                        added.Samples[1],
                    ],
                },
            ],
        };

        CorpusRunReport report = await RunAsync(
            @case,
            new RecordingSequenceFactory(
                CorpusTestData.ExactActual(),
                wrong,
                PostStampActual()));

        Assert.False(report.ProfilePassed);
        Assert.Equal(1, report.Failures[CorpusFailureCategoryV1.UiIntegration]);
        Assert.Equal(1, report.StructuralMismatchCount);
    }

    private static Task<CorpusRunReport> RunAsync(
        ExpressionCaseV1 @case,
        RecordingSequenceFactory factory) => new ExpressionCorpusRunner().RunAsync(
        CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
        factory,
        new CorpusRunOptions(
            CorpusPartitionV1.Development,
            MetricsCapacity: 64,
            RequireMetricCoverage: false),
        default);

    private static ExpressionCaseV1 StampCase()
    {
        ExpressionCaseV1 baseline = CorpusTestData.ValidCase();
        return baseline with
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
                    2,
                    [],
                    [TransformedStroke("stamp-alias")]),
                PostStampStep(),
            ],
        };
    }

    private static RecognizeStepV1 PostStampStep() => new(
        "recognize-2",
        new ExpectedPageV1(
        [
            new ExpectedRegionV1(
                "region-2",
                ["stroke-a", "stroke-b", "stamp-alias"],
                new CorpusBoundsV1(0, 0, 115, 65),
                0.01,
                new AcceptedRegionExpectationV1(
                    "1+5",
                    [
                        new ExpectedTokenV1("token-a", "1", ["stroke-a"]),
                        new ExpectedTokenV1("token-b", "+", ["stroke-b"]),
                        new ExpectedTokenV1("token-stamp", "5", ["stamp-alias"]),
                    ],
                    SequenceLayout("token-a", "token-b", "token-stamp"),
                    null)),
        ],
        null));

    private static RecognizeActualV1 PostStampActual() => new(
        new ActualPageV1(
        [
            new ActualRegionV1(
                "runtime-region-2",
                ["stroke-a", "stroke-b", "actual-stamp-id"],
                new AcceptedRegionActualV1(
                    "1+5",
                    [
                        new ActualTokenV1("1", ["stroke-a"], 0.99, false),
                        new ActualTokenV1("+", ["stroke-b"], 0.99, false),
                        new ActualTokenV1("5", ["actual-stamp-id"], 0.99, false),
                    ],
                    SequenceLayout(0, 1, 2),
                    null),
                new CorpusBoundsV1(0, 0, 115, 65)),
        ],
        null));

    private static ExpectedLayoutNodeV1 SequenceLayout(params string[] tokenIds) => new(
        LayoutKindV1.Sequence,
        [],
        tokenIds.Select(tokenId => new ExpectedLayoutEdgeV1(
            LayoutRoleV1.Item,
            new ExpectedLayoutNodeV1(LayoutKindV1.Token, [tokenId], []))).ToArray());

    private static ActualLayoutNodeV1 SequenceLayout(params int[] tokenIndexes) => new(
        LayoutKindV1.Sequence,
        [],
        tokenIndexes.Select(tokenIndex => new ActualLayoutEdgeV1(
            LayoutRoleV1.Item,
            new ActualLayoutNodeV1(LayoutKindV1.Token, [tokenIndex], []))).ToArray());

    private static StampActualV1 ExactStampActual() => new(
        CorpusStampDecisionV1.Append,
        2,
        [SourceStroke()],
        [],
        [TransformedStroke("actual-stamp-id")],
        new ActualDocumentStateV1(
            ["stroke-a", "stroke-b", "actual-stamp-id"],
            ["stroke-a", "stroke-b"],
            ["actual-stamp-id"]));

    private static CorpusStrokeV1 SourceStroke() => new(
        "answer-source",
        0,
        [
            new CorpusSampleV1(0, 0, 0, 0.4),
            new CorpusSampleV1(10, 10, 10, 0.6),
        ]);

    private static CorpusStrokeV1 TransformedStroke(string id) => new(
        id,
        0,
        [
            new CorpusSampleV1(95, 45, 0, 0.4),
            new CorpusSampleV1(115, 65, 10, 0.6),
        ]);

    private sealed class RecordingSequenceFactory(params StepActualV1[] results)
        : IExpressionScenarioRuntimeFactory
    {
        public List<ScenarioActionV1> Actions { get; } = [];

        public string PipelineFingerprint => "stamp-contract-v1";

        public string ModelFingerprint => new('9', 64);

        public double RecognitionThreshold => 0.75;

        public IExpressionScenarioRuntime Create(ExpressionScenarioInputV1 input, ILocalMetricsSink metrics) =>
            new RecordingRuntime(results, Actions);
    }

    private sealed class RecordingRuntime(
        IEnumerable<StepActualV1> results,
        ICollection<ScenarioActionV1> actions) : IExpressionScenarioRuntime
    {
        private readonly Queue<StepActualV1> _results = new(results);

        public Task<StepActualV1> ApplyAsync(ScenarioActionV1 action, CancellationToken cancellationToken)
        {
            actions.Add(action);
            return Task.FromResult(_results.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
