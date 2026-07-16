using Penumbra.Core;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class RunnerPersistenceModelTests
{
    [Fact]
    public async Task CorruptCurrentRemainsDamagedAndLaterSavePreservesTheKnownGoodBackup()
    {
        ExpectedPageV1 expectedA = ExpectedPageForStrokeA();
        ExpressionCaseV1 @case = CorpusTestData.ValidCase() with
        {
            InitialStrokeIds = ["stroke-a"],
            Steps =
            [
                new SaveStepV1("save-a", "page", CorpusSaveModeV1.Explicit),
                new RewriteStepV1("rewrite-b-1", ["stroke-a"], ["stroke-b"]),
                new SaveStepV1("save-b", "page", CorpusSaveModeV1.Explicit),
                new RecoverStepV1(
                    "recover-a-1",
                    "page",
                    CorpusRecoveryDamageV1.CorruptCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    expectedA),
                new ReopenStepV1(
                    "reopen-still-damaged",
                    "page",
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    expectedA),
                new SaveStepV1("save-recovered-a", "page", CorpusSaveModeV1.Explicit),
                new RecoverStepV1(
                    "recover-a-2",
                    "page",
                    CorpusRecoveryDamageV1.CorruptCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    expectedA),
            ],
        };
        var runtime = new SequenceFactory(
            new PersistenceWriteActualV1(true),
            UserState("stroke-b"),
            new PersistenceWriteActualV1(true),
            RecoveredA(),
            RecoveredA(),
            new PersistenceWriteActualV1(true),
            RecoveredA());

        ExpressionCorpusSuite suite = CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null));
        IReadOnlyList<CorpusValidationError> validation = CorpusValidator.ValidateSuite(suite);
        Assert.True(validation.Count == 0, string.Join(Environment.NewLine, validation));
        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            suite,
            runtime,
            new CorpusRunOptions(
                CorpusPartitionV1.Development,
                MetricsCapacity: 64,
                RequireMetricCoverage: false),
            default);

        Assert.True(report.InfrastructureValid, CorpusReportJson.Serialize(report));
        Assert.True(report.ProfilePassed, CorpusReportJson.Serialize(report));
        Assert.Equal(3, report.ExactExpressionNumerator);
        Assert.Equal(3, report.ExactExpressionDenominator);
        Assert.Equal(0, report.StructuralMismatchCount);
    }

    private static ExpectedPageV1 ExpectedPageForStrokeA() => new(
        [
            new ExpectedRegionV1(
                "region-a",
                ["stroke-a"],
                new CorpusBoundsV1(0, 0, 10, 10),
                0.01,
                new AcceptedRegionExpectationV1(
                    "1",
                    [new ExpectedTokenV1("token-a", "1", ["stroke-a"])],
                    new ExpectedLayoutNodeV1(LayoutKindV1.Token, ["token-a"], []),
                    null)),
        ],
        null);

    private static MutationActualV1 UserState(string strokeId) => new(
        new ActualDocumentStateV1([strokeId], [strokeId], []));

    private static PersistenceOpenActualV1 RecoveredA() => new(
        CorpusOpenStatusV1.BackupRecoveryCandidate,
        new ActualDocumentStateV1(["stroke-a"], ["stroke-a"], []),
        new ActualPageV1(
            [
                new ActualRegionV1(
                    "runtime-region-a",
                    ["stroke-a"],
                    new AcceptedRegionActualV1(
                        "1",
                        [new ActualTokenV1("1", ["stroke-a"], 0.99, false)],
                        new ActualLayoutNodeV1(LayoutKindV1.Token, [0], []),
                        null),
                    new CorpusBoundsV1(0, 0, 10, 10)),
            ],
            null));

    private sealed class SequenceFactory(params StepActualV1[] results) : IExpressionScenarioRuntimeFactory
    {
        public string PipelineFingerprint => "persistence-state-v1";

        public string ModelFingerprint => new('f', 64);

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
