using System.Diagnostics;
using Penumbra.Core;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class RunnerTimeoutTests
{
    // The configured runner deadlines below prove product timeout behavior. This outer wall-clock check
    // exists only to catch an unbounded hang, so it must tolerate thread-pool saturation when every test
    // project runs concurrently in the solution gate.
    private static readonly TimeSpan UnboundedHangGuard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NeverCompletingApplyIsBoundedEvenWhenRuntimeIgnoresCancellation()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        var stopwatch = Stopwatch.StartNew();

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new HangingRuntimeFactory(hangApply: true, hangDispose: false),
            TightDiagnosticOptions(),
            default);

        Assert.True(stopwatch.Elapsed < UnboundedHangGuard);
        Assert.False(report.InfrastructureValid);
        Assert.False(report.ProfilePassed);
        Assert.True(report.Failures[CorpusFailureCategoryV1.Infrastructure] > 0);
    }

    [Fact]
    public async Task NeverCompletingDisposeIsBounded()
    {
        ExpressionCaseV1 @case = CorpusTestData.ValidCase();
        var stopwatch = Stopwatch.StartNew();

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            new HangingRuntimeFactory(hangApply: false, hangDispose: true),
            TightDiagnosticOptions(),
            default);

        Assert.True(stopwatch.Elapsed < UnboundedHangGuard);
        Assert.False(report.InfrastructureValid);
        Assert.False(report.ProfilePassed);
        Assert.True(report.Failures[CorpusFailureCategoryV1.Infrastructure] > 0);
    }

    private static CorpusRunOptions TightDiagnosticOptions() => new(
        CorpusPartitionV1.Development,
        MetricsCapacity: 64,
        RequireMetricCoverage: false,
        StepTimeout: TimeSpan.FromMilliseconds(25),
        CaseTimeout: TimeSpan.FromMilliseconds(150),
        SuiteTimeout: TimeSpan.FromMilliseconds(500),
        DisposalTimeout: TimeSpan.FromMilliseconds(25));

    private sealed class HangingRuntimeFactory(bool hangApply, bool hangDispose)
        : IExpressionScenarioRuntimeFactory
    {
        public string PipelineFingerprint => "timeout-test-v1";

        public string ModelFingerprint => new('e', 64);

        public double RecognitionThreshold => 0.75;

        public IExpressionScenarioRuntime Create(ExpressionScenarioInputV1 input, ILocalMetricsSink metrics) =>
            new HangingRuntime(hangApply, hangDispose);
    }

    private sealed class HangingRuntime(bool hangApply, bool hangDispose) : IExpressionScenarioRuntime
    {
        private readonly TaskCompletionSource<StepActualV1> _apply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _dispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StepActualV1> ApplyAsync(ScenarioActionV1 action, CancellationToken cancellationToken) =>
            hangApply ? _apply.Task : Task.FromResult<StepActualV1>(CorpusTestData.ExactActual());

        public ValueTask DisposeAsync() => hangDispose
            ? new ValueTask(_dispose.Task)
            : ValueTask.CompletedTask;
    }
}
