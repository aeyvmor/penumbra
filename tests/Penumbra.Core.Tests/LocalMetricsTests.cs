using Penumbra.Core;

namespace Penumbra.Core.Tests;

/// <summary>
/// Pins the local-only metrics seam: fixed-cardinality observations, global bounded retention,
/// completed-only nearest-rank percentiles, and exactly-once timing scopes on controllable time.
/// </summary>
public sealed class LocalMetricsTests
{
    [Fact]
    public void OperationAndOutcomeSets_AreFixedAndExplicit()
    {
        Assert.Equal(
            new[]
            {
                MetricOperation.RecognitionQuietPeriod,
                MetricOperation.RecognitionProcessing,
                MetricOperation.RecognitionPartition,
                MetricOperation.RecognitionClassification,
                MetricOperation.RecognitionGrammar,
                MetricOperation.SheetRecompute,
                MetricOperation.TaffyProcessing,
                MetricOperation.TaffyProbe,
                MetricOperation.TaffyGhostSynthesis,
                MetricOperation.TaffyPublication,
                MetricOperation.ExplicitSave,
                MetricOperation.Autosave,
                MetricOperation.RecoveryRead,
                MetricOperation.CloseFlush,
                MetricOperation.GraphDetection,
                MetricOperation.GraphSampling,
            },
            Enum.GetValues<MetricOperation>());
        Assert.Equal(
            Enumerable.Range(0, 16),
            Enum.GetValues<MetricOperation>().Select(operation => (int)operation));

        Assert.Equal(
            new[]
            {
                MetricOutcome.Completed,
                MetricOutcome.Cancelled,
                MetricOutcome.Refused,
                MetricOutcome.Failed,
            },
            Enum.GetValues<MetricOutcome>());
        Assert.Equal(
            Enumerable.Range(0, 4),
            Enum.GetValues<MetricOutcome>().Select(outcome => (int)outcome));
    }

    [Fact]
    public void ObservationAndSummary_ContainOnlyFixedOrNumericFields()
    {
        Type[] allowedTypes =
        {
            typeof(MetricOperation),
            typeof(MetricOutcome),
            typeof(TimeSpan),
            typeof(TimeSpan?),
            typeof(int),
            typeof(int?),
        };

        var observationProperties = typeof(MetricObservation).GetProperties();
        var summaryProperties = typeof(MetricOperationSummary).GetProperties();

        Assert.Equal(4, observationProperties.Length);
        Assert.Equal(8, summaryProperties.Length);
        var dataProperties = observationProperties.Concat(summaryProperties).ToArray();
        var dataFields = typeof(MetricObservation)
            .GetFields(System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
            .Concat(typeof(MetricOperationSummary).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic))
            .ToArray();

        Assert.All(dataProperties, property =>
            Assert.Contains(property.PropertyType, allowedTypes));
        Assert.All(dataFields, field => Assert.Contains(field.FieldType, allowedTypes));
        Assert.DoesNotContain(dataProperties, property => property.PropertyType == typeof(string));
        Assert.DoesNotContain(dataFields, field => field.FieldType == typeof(string));
        Assert.DoesNotContain(dataProperties, property => property.PropertyType == typeof(Guid));
        Assert.DoesNotContain(dataFields, field => field.FieldType == typeof(Guid));
    }

    [Fact]
    public void Observation_ValidatesDurationAndItemCount()
    {
        var zero = new MetricObservation(
            MetricOperation.SheetRecompute,
            MetricOutcome.Completed,
            TimeSpan.Zero,
            0);

        Assert.Equal(TimeSpan.Zero, zero.Duration);
        Assert.Equal(0, zero.ItemCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricObservation(
            MetricOperation.SheetRecompute,
            MetricOutcome.Completed,
            TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricObservation(
            MetricOperation.SheetRecompute,
            MetricOutcome.Completed,
            TimeSpan.Zero,
            -1));
    }

    [Fact]
    public void Observation_RejectsUnknownEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricObservation(
            (MetricOperation)999,
            MetricOutcome.Completed,
            TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricObservation(
            MetricOperation.Autosave,
            (MetricOutcome)999,
            TimeSpan.Zero));
    }

    [Fact]
    public void NoOpSink_IsASingletonAndAcceptsObservations()
    {
        Assert.Same(NoOpLocalMetricsSink.Instance, NoOpLocalMetricsSink.Instance);

        NoOpLocalMetricsSink.Instance.Record(Observation(MetricOperation.RecognitionGrammar, 12));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedInMemoryMetricsSink(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedInMemoryMetricsSink(-1));
    }

    [Fact]
    public void Record_EvictsTheOldestObservationGlobally_AndPreservesOrder()
    {
        var sink = new BoundedInMemoryMetricsSink(3);
        var evicted = Observation(MetricOperation.RecognitionGrammar, 1);
        var retainedFirst = Observation(MetricOperation.SheetRecompute, 2, itemCount: 4);
        var retainedSecond = Observation(MetricOperation.RecognitionClassification, 3);
        var retainedThird = Observation(MetricOperation.TaffyPublication, 4);

        sink.Record(evicted);
        sink.Record(retainedFirst);
        sink.Record(retainedSecond);
        sink.Record(retainedThird);

        LocalMetricsSnapshot snapshot = sink.Snapshot();
        Assert.Equal(new[] { retainedFirst, retainedSecond, retainedThird }, snapshot.Observations);
        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(0, snapshot.SummaryFor(MetricOperation.RecognitionGrammar).SampleCount);
        Assert.Equal(1, snapshot.SummaryFor(MetricOperation.SheetRecompute).SampleCount);
    }

    [Fact]
    public void Snapshot_IsIndependentFromLaterRecords()
    {
        var sink = new BoundedInMemoryMetricsSink(2);
        var first = Observation(MetricOperation.ExplicitSave, 1);
        var second = Observation(MetricOperation.Autosave, 2);
        sink.Record(first);

        LocalMetricsSnapshot before = sink.Snapshot();
        sink.Record(second);

        Assert.Equal(new[] { first }, before.Observations);
        Assert.Equal(new[] { first, second }, sink.Snapshot().Observations);
    }

    [Fact]
    public void Snapshot_Collections_CannotBeMutatedByCallers()
    {
        var sink = new BoundedInMemoryMetricsSink(1);
        var observation = Observation(MetricOperation.ExplicitSave, 1);
        sink.Record(observation);
        LocalMetricsSnapshot snapshot = sink.Snapshot();

        var observations = Assert.IsAssignableFrom<IList<MetricObservation>>(snapshot.Observations);
        var summaries = Assert.IsAssignableFrom<IList<MetricOperationSummary>>(snapshot.Summaries);

        Assert.True(observations.IsReadOnly);
        Assert.True(summaries.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => observations[0] = observation);
        Assert.Throws<NotSupportedException>(() => observations.Add(observation));
        Assert.Throws<NotSupportedException>(() => summaries[0] = summaries[0]);
        Assert.Throws<NotSupportedException>(() => summaries.Add(summaries[0]));
    }

    [Fact]
    public void Summary_SeparatesAllOutcomes_AndExcludesNonCompletedDurations()
    {
        var sink = new BoundedInMemoryMetricsSink(8);
        sink.Record(Observation(MetricOperation.RecoveryRead, 11, MetricOutcome.Completed));
        sink.Record(Observation(MetricOperation.RecoveryRead, 999, MetricOutcome.Cancelled));
        sink.Record(Observation(MetricOperation.RecoveryRead, 888, MetricOutcome.Refused));
        sink.Record(Observation(MetricOperation.RecoveryRead, 777, MetricOutcome.Failed));

        MetricOperationSummary summary = sink.Snapshot().SummaryFor(MetricOperation.RecoveryRead);

        Assert.Equal(4, summary.SampleCount);
        Assert.Equal(1, summary.CompletedCount);
        Assert.Equal(1, summary.CancelledCount);
        Assert.Equal(1, summary.RefusedCount);
        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(TimeSpan.FromMilliseconds(11), summary.CompletedDurationP50);
        Assert.Equal(TimeSpan.FromMilliseconds(11), summary.CompletedDurationP95);
    }

    [Fact]
    public void Summary_WithNoCompletedSamples_HasNoPercentiles()
    {
        var sink = new BoundedInMemoryMetricsSink(2);
        sink.Record(Observation(MetricOperation.CloseFlush, 3, MetricOutcome.Cancelled));

        MetricOperationSummary summary = sink.Snapshot().SummaryFor(MetricOperation.CloseFlush);

        Assert.Null(summary.CompletedDurationP50);
        Assert.Null(summary.CompletedDurationP95);
    }

    [Fact]
    public void Summary_UsesNearestRankForOneSample()
    {
        var sink = new BoundedInMemoryMetricsSink(1);
        sink.Record(Observation(MetricOperation.GraphDetection, 42));

        MetricOperationSummary summary = sink.Snapshot().SummaryFor(MetricOperation.GraphDetection);

        Assert.Equal(TimeSpan.FromMilliseconds(42), summary.CompletedDurationP50);
        Assert.Equal(TimeSpan.FromMilliseconds(42), summary.CompletedDurationP95);
    }

    [Fact]
    public void Summary_UsesNearestRankForTwoSamples()
    {
        var sink = new BoundedInMemoryMetricsSink(2);
        sink.Record(Observation(MetricOperation.GraphSampling, 20));
        sink.Record(Observation(MetricOperation.GraphSampling, 10));

        MetricOperationSummary summary = sink.Snapshot().SummaryFor(MetricOperation.GraphSampling);

        Assert.Equal(TimeSpan.FromMilliseconds(10), summary.CompletedDurationP50);
        Assert.Equal(TimeSpan.FromMilliseconds(20), summary.CompletedDurationP95);
    }

    [Fact]
    public void Summary_PercentilesExcludeEvictedSamplesFromTheSameOperation()
    {
        var sink = new BoundedInMemoryMetricsSink(2);
        sink.Record(Observation(MetricOperation.RecognitionProcessing, 100));
        sink.Record(Observation(MetricOperation.RecognitionProcessing, 1));
        sink.Record(Observation(MetricOperation.RecognitionProcessing, 2));

        MetricOperationSummary summary = sink.Snapshot().SummaryFor(MetricOperation.RecognitionProcessing);

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(TimeSpan.FromMilliseconds(1), summary.CompletedDurationP50);
        Assert.Equal(TimeSpan.FromMilliseconds(2), summary.CompletedDurationP95);
    }

    [Fact]
    public void Summary_UsesNearestRankForTwentySamples()
    {
        var sink = new BoundedInMemoryMetricsSink(20);
        for (int milliseconds = 20; milliseconds >= 1; milliseconds--)
        {
            sink.Record(Observation(MetricOperation.RecognitionPartition, milliseconds));
        }

        MetricOperationSummary summary = sink.Snapshot().SummaryFor(MetricOperation.RecognitionPartition);

        Assert.Equal(TimeSpan.FromMilliseconds(10), summary.CompletedDurationP50);
        Assert.Equal(TimeSpan.FromMilliseconds(19), summary.CompletedDurationP95);
    }

    [Fact]
    public void Snapshot_SummariesFollowEnumOrder_AndIncludeEmptyOperations()
    {
        var sink = new BoundedInMemoryMetricsSink(1);
        sink.Record(Observation(MetricOperation.TaffyProbe, 1));

        LocalMetricsSnapshot snapshot = sink.Snapshot();

        Assert.Equal(Enum.GetValues<MetricOperation>(), snapshot.Summaries.Select(summary => summary.Operation));
        Assert.Equal(Enum.GetValues<MetricOperation>().Length, snapshot.Summaries.Count);
        Assert.Equal(1, snapshot.Summaries.Sum(summary => summary.SampleCount));
    }

    [Fact]
    public void ItemCount_IsRetainedWithoutChangingDurationSummary()
    {
        var sink = new BoundedInMemoryMetricsSink(2);
        sink.Record(Observation(MetricOperation.SheetRecompute, 8, itemCount: 17));
        sink.Record(Observation(MetricOperation.SheetRecompute, 12));

        LocalMetricsSnapshot snapshot = sink.Snapshot();

        Assert.Equal(17, snapshot.Observations[0].ItemCount);
        Assert.Null(snapshot.Observations[1].ItemCount);
        Assert.Equal(TimeSpan.FromMilliseconds(8), snapshot.SummaryFor(MetricOperation.SheetRecompute).CompletedDurationP50);
    }

    [Fact]
    public void ConcurrentRecord_NeverExceedsTheGlobalBound()
    {
        const int capacity = 64;
        var sink = new BoundedInMemoryMetricsSink(capacity);

        Parallel.For(0, 10_000, index => sink.Record(new MetricObservation(
            (MetricOperation)(index % Enum.GetValues<MetricOperation>().Length),
            (MetricOutcome)(index % Enum.GetValues<MetricOutcome>().Length),
            TimeSpan.FromTicks(index),
            index)));

        LocalMetricsSnapshot snapshot = sink.Snapshot();

        Assert.Equal(capacity, snapshot.SampleCount);
        Assert.Equal(capacity, snapshot.Observations.Count);
        Assert.Equal(capacity, snapshot.Summaries.Sum(summary => summary.SampleCount));
        Assert.All(snapshot.Observations, observation => Assert.InRange(observation.ItemCount!.Value, 0, 9_999));
    }

    [Fact]
    public void TimingScope_RecordsElapsedTimeAndItemCountOnComplete()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(1);

        using (var scope = MetricTimingScope.Start(sink, MetricOperation.SheetRecompute, time))
        {
            time.Advance(TimeSpan.FromMilliseconds(17));
            scope.Complete(6);
        }

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        Assert.Equal(MetricOperation.SheetRecompute, observation.Operation);
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(17), observation.Duration);
        Assert.Equal(6, observation.ItemCount);
    }

    [Fact]
    public void TimingScope_StartValidatesSinkClockAndOperationUpFront()
    {
        var sink = new BoundedInMemoryMetricsSink(1);
        var time = new ManualTimestampTimeProvider();

        Assert.Throws<ArgumentNullException>(() => MetricTimingScope.Start(
            null!,
            MetricOperation.RecognitionGrammar,
            time));
        Assert.Throws<ArgumentNullException>(() => MetricTimingScope.Start(
            sink,
            MetricOperation.RecognitionGrammar,
            null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricTimingScope.Start(
            sink,
            (MetricOperation)999,
            time));
    }

    [Fact]
    public void TimingScope_NoOpSink_ReusesAStatelessScopeWithoutReadingTheClock()
    {
        var firstTime = new ManualTimestampTimeProvider();
        var secondTime = new ManualTimestampTimeProvider(1_000);

        MetricTimingScope first = MetricTimingScope.Start(
            NoOpLocalMetricsSink.Instance,
            MetricOperation.RecognitionProcessing,
            firstTime);
        MetricTimingScope second = MetricTimingScope.Start(
            NoOpLocalMetricsSink.Instance,
            MetricOperation.Autosave,
            secondTime);

        Assert.Same(first, second);
        first.Complete(3);
        second.Dispose();
        Assert.Equal(0, firstTime.TimestampReads);
        Assert.Equal(0, secondTime.TimestampReads);
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Fail(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricTimingScope.Start(
            NoOpLocalMetricsSink.Instance,
            (MetricOperation)999,
            firstTime));
        Assert.Equal(0, firstTime.TimestampReads);
    }

    [Fact]
    public void TimingScope_UsesInjectedNonstandardTimestampFrequency()
    {
        var time = new ManualTimestampTimeProvider(1_000);
        var sink = new BoundedInMemoryMetricsSink(1);
        var scope = MetricTimingScope.Start(sink, MetricOperation.RecognitionProcessing, time);
        time.Advance(TimeSpan.FromMilliseconds(125));

        scope.Complete();

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        Assert.Equal(TimeSpan.FromMilliseconds(125), observation.Duration);
    }

    [Fact]
    public void TimingScope_RecordsEveryExplicitTerminalOutcome()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(4);

        MetricTimingScope completed = MetricTimingScope.Start(sink, MetricOperation.TaffyProbe, time);
        time.Advance(TimeSpan.FromMilliseconds(1));
        completed.Complete();

        MetricTimingScope cancelled = MetricTimingScope.Start(sink, MetricOperation.TaffyProbe, time);
        time.Advance(TimeSpan.FromMilliseconds(2));
        cancelled.Cancel();

        MetricTimingScope refused = MetricTimingScope.Start(sink, MetricOperation.TaffyProbe, time);
        time.Advance(TimeSpan.FromMilliseconds(3));
        refused.Refuse();

        MetricTimingScope failed = MetricTimingScope.Start(sink, MetricOperation.TaffyProbe, time);
        time.Advance(TimeSpan.FromMilliseconds(4));
        failed.Fail();

        Assert.Equal(
            new[]
            {
                MetricOutcome.Completed,
                MetricOutcome.Cancelled,
                MetricOutcome.Refused,
                MetricOutcome.Failed,
            },
            sink.Snapshot().Observations.Select(observation => observation.Outcome));
    }

    [Fact]
    public void TimingScope_DisposeWithoutTerminalOutcome_RecordsCancellation()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(1);

        using (MetricTimingScope.Start(sink, MetricOperation.RecognitionClassification, time))
        {
            time.Advance(TimeSpan.FromMilliseconds(9));
        }

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        Assert.Equal(MetricOutcome.Cancelled, observation.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(9), observation.Duration);
    }

    [Fact]
    public void TimingScope_FirstTerminalCallWins_AndDisposeCannotRecordAgain()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(4);
        var scope = MetricTimingScope.Start(sink, MetricOperation.RecognitionGrammar, time);
        time.Advance(TimeSpan.FromMilliseconds(5));

        scope.Complete(2);
        time.Advance(TimeSpan.FromMilliseconds(50));
        scope.Complete(3);
        scope.Fail(4);
        scope.Dispose();
        scope.Dispose();

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        Assert.Equal(MetricOutcome.Completed, observation.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(5), observation.Duration);
        Assert.Equal(2, observation.ItemCount);
        Assert.Equal(2, time.TimestampReads);   // start + winning end; losers never touch the clock
    }

    [Fact]
    public void TimingScope_ConcurrentTerminalCalls_RecordExactlyOnce()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(4);
        var scope = MetricTimingScope.Start(sink, MetricOperation.Autosave, time);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Parallel.Invoke(
            () => scope.Complete(),
            () => scope.Cancel(),
            () => scope.Refuse(),
            () => scope.Fail(),
            scope.Dispose);

        Assert.Single(sink.Snapshot().Observations);
    }

    [Fact]
    public void TimingScope_ValidatesItemCountBeforeRecording()
    {
        var sink = new BoundedInMemoryMetricsSink(1);
        var scope = MetricTimingScope.Start(sink, MetricOperation.GraphSampling, new ManualTimestampTimeProvider());

        Assert.Throws<ArgumentOutOfRangeException>(() => scope.Complete(-1));
        scope.Refuse(0);

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        Assert.Equal(MetricOutcome.Refused, observation.Outcome);
        Assert.Equal(0, observation.ItemCount);
    }

    [Fact]
    public void TimingScope_InvalidClockDuration_DoesNotConsumeTheTerminalOutcome()
    {
        var time = new ManualTimestampTimeProvider();
        var sink = new BoundedInMemoryMetricsSink(1);
        var scope = MetricTimingScope.Start(sink, MetricOperation.GraphDetection, time);
        time.Advance(TimeSpan.FromTicks(-1));

        Assert.Throws<ArgumentOutOfRangeException>(() => scope.Complete());

        time.Advance(TimeSpan.FromTicks(2));
        scope.Fail();

        MetricObservation observation = Assert.Single(sink.Snapshot().Observations);
        Assert.Equal(MetricOutcome.Failed, observation.Outcome);
        Assert.Equal(TimeSpan.FromTicks(1), observation.Duration);
    }

    private static MetricObservation Observation(
        MetricOperation operation,
        int milliseconds,
        MetricOutcome outcome = MetricOutcome.Completed,
        int? itemCount = null) =>
        new(operation, outcome, TimeSpan.FromMilliseconds(milliseconds), itemCount);

    /// <summary>A monotonic timestamp source advanced directly by tests; no timers or sleeps.</summary>
    private sealed class ManualTimestampTimeProvider : TimeProvider
    {
        private long _timestamp;
        private int _timestampReads;
        private readonly long _timestampFrequency;

        public ManualTimestampTimeProvider(long timestampFrequency = TimeSpan.TicksPerSecond)
        {
            _timestampFrequency = timestampFrequency;
        }

        public override long TimestampFrequency => _timestampFrequency;

        public int TimestampReads => Volatile.Read(ref _timestampReads);

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _timestampReads);
            return Interlocked.Read(ref _timestamp);
        }

        public void Advance(TimeSpan duration)
        {
            long timestampDelta = checked((long)(duration.Ticks * (decimal)_timestampFrequency /
                TimeSpan.TicksPerSecond));
            Interlocked.Add(ref _timestamp, timestampDelta);
        }
    }
}
