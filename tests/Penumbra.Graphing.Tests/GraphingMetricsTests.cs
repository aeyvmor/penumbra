using Penumbra.Core;

namespace Penumbra.Graphing.Tests;

/// <summary>
/// Proves Graphing's <see cref="MetricOperation.GraphDetection"/>/<see cref="MetricOperation.GraphSampling"/>
/// observations follow the Phase 5.5 ledger discipline: a clean business rejection/refusal records
/// <see cref="MetricOutcome.Refused"/>, an internal translate/parse/compile exception caught at the API
/// boundary records <see cref="MetricOutcome.Failed"/> (never a refusal, even though the caller only ever
/// sees a typed rejection — never a thrown exception), and success records <see cref="MetricOutcome.Completed"/>
/// with the produced item count.
/// </summary>
public sealed class GraphingMetricsTests
{
    [Fact]
    public void Detect_Accepted_RecordsCompletedWithItemCountOne()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector(sink);

        var outcome = detector.Detect("y=x^2");

        Assert.True(outcome.IsAccepted);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.GraphDetection));
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(1, observation.ItemCount);
    }

    [Theory]
    [InlineData("x^2+1")] // NotAnEquation
    [InlineData("2x=6")] // LhsNotBareVariable
    [InlineData("a=2")] // ConstantRhs
    [InlineData("z=x+y")] // MultipleFreeVariables
    public void Detect_CleanRejection_RecordsRefusedNotFailed(string latex)
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector(sink);

        var outcome = detector.Detect(latex);

        Assert.False(outcome.IsAccepted);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.GraphDetection));
        Assert.Equal(MetricOutcome.Refused, observation.Outcome);
    }

    [Fact]
    public void Detect_TranslatorException_RecordsFailedNotRefused()
    {
        // \pm throws NotSupportedException inside LatexToAngouriMath.Translate — an exception path. Even
        // though Detect() never lets that exception reach the caller (it comes back as a typed rejection),
        // the metric must record Failed, per the Phase 5.5 ledger's "known exception paths call Fail" rule.
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector(sink);

        var outcome = detector.Detect(@"y=x\pm1");

        Assert.False(outcome.IsAccepted);
        Assert.Equal(GraphRejectionReason.UnsupportedConstruct, outcome.Reason);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.GraphDetection));
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
    }

    [Fact]
    public void Detect_NullArgument_RecordsNoObservation()
    {
        // A guard-clause violation is not a metered operation at all (mirrors SheetGraph's precedent of
        // null-checking outside any timing scope).
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector(sink);

        Assert.Throws<ArgumentNullException>(() => detector.Detect((string)null!));

        Assert.Empty(sink.Snapshot().Observations);
    }

    [Fact]
    public void SampleSeries_Accepted_RecordsCompletedWithPointCount()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector();
        var sampler = new DomainSampler(sink);
        var candidate = detector.Detect("y=x").Candidate!;

        var outcome = sampler.SampleSeries(candidate, GraphDomain.Create(-2, 2), 5);

        Assert.True(outcome.IsSampled);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.GraphSampling));
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(5, observation.ItemCount);
    }

    [Fact]
    public void SampleSeries_AllNonFiniteDomain_IsStillCompletedWithZeroItems()
    {
        // A domain that yields no finite point (e.g. sqrt over an entirely negative range) is honest output,
        // not a business refusal — only an uncompilable expression refuses.
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector();
        var sampler = new DomainSampler(sink);
        var candidate = detector.Detect(@"y=\sqrt{x}").Candidate!;

        var outcome = sampler.SampleSeries(candidate, GraphDomain.Create(-5, -1), 5);

        Assert.True(outcome.IsSampled);
        Assert.Empty(outcome.Series!.Segments);
        MetricObservation observation = Assert.Single(ObservationsFor(sink, MetricOperation.GraphSampling));
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(0, observation.ItemCount);
    }

    [Fact]
    public void SampleSeries_InvalidArgument_RecordsNoObservation()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        var detector = new GraphDetector();
        var sampler = new DomainSampler(sink);
        var candidate = detector.Detect("y=x").Candidate!;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => sampler.SampleSeries(candidate, GraphDomain.Create(-1, 1), sampleCount: 1));

        Assert.Empty(sink.Snapshot().Observations);
    }

    private static MetricObservation[] ObservationsFor(BoundedInMemoryMetricsSink sink, MetricOperation operation) =>
        sink.Snapshot().Observations.Where(observation => observation.Operation == operation).ToArray();
}
