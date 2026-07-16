using Penumbra.Core;

namespace Penumbra.Core.Tests;

public sealed class PageAutosaveCoordinatorTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(750);

    [Fact]
    public async Task ScheduleCoalescesToLatestSnapshotAfterAFullQuietPeriod()
    {
        var time = new FakeTimeProvider();
        var store = new ControlledPageStore();
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, time);

        Assert.Equal(1, coordinator.Schedule(Document("first"), "page.pen"));
        time.Advance(Quiet - TimeSpan.FromMilliseconds(1));
        Assert.Empty(store.Calls);

        Assert.Equal(2, coordinator.Schedule(Document("latest"), "page.pen"));
        time.Advance(Quiet - TimeSpan.FromMilliseconds(1));
        Assert.Empty(store.Calls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => store.Calls.Count == 1);

        SaveCall call = Assert.Single(store.Calls);
        Assert.Equal(2, call.Generation);
        Assert.Equal("latest", call.Document.Variables["marker"]);
        Assert.Equal(PageSaveKind.Autosave, call.Kind);
        Assert.Equal(2, coordinator.CommittedRevision);
        Assert.Null(coordinator.LastFailure);
    }

    [Fact]
    public async Task FlushBypassesDebounceAndWaitsForDurability()
    {
        var time = new FakeTimeProvider();
        var store = new ControlledPageStore();
        var metrics = new BoundedInMemoryMetricsSink(8);
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, time, metrics);
        coordinator.Schedule(Document("flush"), "page.pen");

        await coordinator.FlushAsync();

        Assert.Single(store.Calls);
        Assert.Equal(1, coordinator.CommittedRevision);
        MetricObservation close = Assert.Single(
            metrics.Snapshot().Observations,
            item => item.Operation == MetricOperation.CloseFlush);
        Assert.Equal(MetricOutcome.Completed, close.Outcome);
    }

    [Fact]
    public async Task FlushInvokesTheStoreAwayFromItsCallerContext()
    {
        SynchronizationContext? storeContext = null;
        var store = new ControlledPageStore(call =>
        {
            storeContext = SynchronizationContext.Current;
            return Task.FromResult(new PageSaveResult(PageSaveStatus.Committed, call.Generation));
        });
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet);
        coordinator.Schedule(Document("worker"), "page.pen");
        var callerContext = new SynchronizationContext();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task flush;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            flush = coordinator.FlushAsync();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await flush;

        Assert.NotSame(callerContext, storeContext);
    }

    [Fact]
    public async Task NewScheduleCancelsInFlightOlderWriteAndSerializesTheWinner()
    {
        var time = new FakeTimeProvider();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledPageStore(async call =>
        {
            if (call.Generation == 1)
            {
                firstEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.CancellationToken);
            }

            return new PageSaveResult(PageSaveStatus.Committed, call.Generation);
        });
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, time);

        coordinator.Schedule(Document("old"), "page.pen");
        time.Advance(Quiet);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.Schedule(Document("new"), "page.pen");
        time.Advance(Quiet);
        await EventuallyAsync(() => coordinator.CommittedRevision == 2);

        Assert.Equal(new long[] { 1, 2 }, store.Calls.Select(call => call.Generation));
        Assert.Equal(1, store.MaxConcurrentCalls);
        Assert.Null(coordinator.LastFailure);
    }

    [Fact]
    public async Task FlushFollowsANewerRevisionScheduledDuringItsWrite()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledPageStore(async call =>
        {
            if (call.Generation == 1)
            {
                firstEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.CancellationToken);
            }

            return new PageSaveResult(PageSaveStatus.Committed, call.Generation);
        });
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet);
        coordinator.Schedule(Document("old"), "page.pen");
        Task flush = coordinator.FlushAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Schedule(Document("new"), "page.pen");
        await flush.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, coordinator.LatestRevision);
        Assert.Equal(2, coordinator.CommittedRevision);
        Assert.Equal(new long[] { 1, 2 }, store.Calls.Select(call => call.Generation));
    }

    [Fact]
    public async Task NonCooperativeOlderWriteMayFinishButLatestStillCommitsLast()
    {
        var time = new FakeTimeProvider();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ControlledPageStore(async call =>
        {
            if (call.Generation == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            }

            return new PageSaveResult(PageSaveStatus.Committed, call.Generation);
        });
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, time);
        coordinator.Schedule(Document("old"), "page.pen");
        time.Advance(Quiet);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        coordinator.Schedule(Document("new"), "page.pen");
        Task flush = coordinator.FlushAsync();
        releaseFirst.TrySetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new long[] { 1, 2 }, store.Calls.Select(call => call.Generation));
        Assert.Equal(2, coordinator.CommittedRevision);
        Assert.Equal(1, store.MaxConcurrentCalls);
    }

    [Fact]
    public async Task BackgroundFailureIsObservedAndFlushCanRetryTheSameRevision()
    {
        var time = new FakeTimeProvider();
        int attempts = 0;
        var store = new ControlledPageStore(call =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("injected write failure");
            }

            return Task.FromResult(new PageSaveResult(PageSaveStatus.Committed, call.Generation));
        });
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, time);
        var states = new System.Collections.Concurrent.ConcurrentQueue<PageAutosaveStateChangedEventArgs>();
        coordinator.StateChanged += (_, state) => states.Enqueue(state);
        coordinator.Schedule(Document("retry"), "page.pen");
        time.Advance(Quiet);
        await EventuallyAsync(() => coordinator.LastFailure is IOException && states.Count == 1);

        PageAutosaveStateChangedEventArgs failed = Assert.Single(states);
        Assert.Equal(1, failed.Revision);
        Assert.False(failed.Committed);
        Assert.IsType<IOException>(failed.Failure);

        await coordinator.FlushAsync();

        Assert.Equal(2, attempts);
        Assert.Equal(1, coordinator.CommittedRevision);
        Assert.Null(coordinator.LastFailure);
        await EventuallyAsync(() => states.Count == 2);
        Assert.Collection(
            states,
            state => Assert.False(state.Committed),
            state =>
            {
                Assert.Equal(1, state.Revision);
                Assert.True(state.Committed);
                Assert.Null(state.Failure);
            });
    }

    [Fact]
    public async Task ObserverFailureCannotBreakADurableAutosave()
    {
        var time = new FakeTimeProvider();
        var store = new ControlledPageStore();
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, time);
        var observed = new TaskCompletionSource<PageAutosaveStateChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, _) => throw new InvalidOperationException("hostile observer");
        coordinator.StateChanged += (_, state) => observed.TrySetResult(state);

        coordinator.Schedule(Document("observer"), "page.pen");
        time.Advance(Quiet);
        PageAutosaveStateChangedEventArgs state = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(state.Committed);
        Assert.Equal(1, coordinator.CommittedRevision);
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task ReentrantObserverCanDisposeWithoutDeadlockingItsPersistenceTask()
    {
        var time = new FakeTimeProvider();
        var store = new ControlledPageStore();
        var coordinator = new PageAutosaveCoordinator(store, Quiet, time);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.StateChanged += (_, _) =>
        {
            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            disposed.TrySetResult();
        };

        coordinator.Schedule(Document("dispose-observer"), "page.pen");
        time.Advance(Quiet);

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, coordinator.CommittedRevision);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task CancelledFlushRecordsCancellationAndLeavesRevisionPending()
    {
        var store = new ControlledPageStore(async call =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, call.CancellationToken);
            return new PageSaveResult(PageSaveStatus.Committed, call.Generation);
        });
        var metrics = new BoundedInMemoryMetricsSink(8);
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, metricsSink: metrics);
        coordinator.Schedule(Document("cancel"), "page.pen");
        using var cancellation = new CancellationTokenSource();
        Task flush = coordinator.FlushAsync(cancellation.Token);
        await EventuallyAsync(() => store.Calls.Count == 1);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush);

        Assert.Equal(0, coordinator.CommittedRevision);
        MetricObservation close = Assert.Single(
            metrics.Snapshot().Observations,
            item => item.Operation == MetricOperation.CloseFlush);
        Assert.Equal(MetricOutcome.Cancelled, close.Outcome);
    }

    [Fact]
    public async Task FailedFlushRecordsFailureWithoutLeakingPageDimensions()
    {
        var store = new ControlledPageStore(_ => throw new IOException("injected"));
        var metrics = new BoundedInMemoryMetricsSink(8);
        await using var coordinator = new PageAutosaveCoordinator(store, Quiet, metricsSink: metrics);
        coordinator.Schedule(Document("private-expression"), "private-path.pen");

        await Assert.ThrowsAsync<IOException>(() => coordinator.FlushAsync());

        MetricObservation close = Assert.Single(
            metrics.Snapshot().Observations,
            item => item.Operation == MetricOperation.CloseFlush);
        Assert.Equal(MetricOutcome.Failed, close.Outcome);
        Assert.Null(close.ItemCount);
    }

    [Fact]
    public async Task DisposeCancelsPendingDelayAndRejectsFurtherWork()
    {
        var time = new FakeTimeProvider();
        var store = new ControlledPageStore();
        var coordinator = new PageAutosaveCoordinator(store, Quiet, time);
        coordinator.Schedule(Document("pending"), "page.pen");

        await coordinator.DisposeAsync();
        time.Advance(TimeSpan.FromDays(1));

        Assert.Empty(store.Calls);
        Assert.Throws<ObjectDisposedException>(() => coordinator.Schedule(Document("late"), "page.pen"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.FlushAsync());
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task RejectsInvalidConstructionAndScheduleArguments()
    {
        var store = new ControlledPageStore();
        Assert.Throws<ArgumentNullException>(() => new PageAutosaveCoordinator(null!, Quiet));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageAutosaveCoordinator(store, TimeSpan.Zero));

        await using var coordinator = new PageAutosaveCoordinator(store, Quiet);
        Assert.Throws<ArgumentNullException>(() => coordinator.Schedule(null!, "page.pen"));
        Assert.Throws<ArgumentException>(() => coordinator.Schedule(Document("x"), " "));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PageAutosaveStateChangedEventArgs(0, committed: true, failure: null));
        Assert.Throws<ArgumentException>(() =>
            new PageAutosaveStateChangedEventArgs(1, committed: true, new IOException()));
        Assert.Throws<ArgumentException>(() =>
            new PageAutosaveStateChangedEventArgs(1, committed: false, failure: null));
    }

    private static PenumbraDocument Document(string marker) =>
        PenumbraDocumentSerializer.CreateEmpty() with
        {
            Variables = new Dictionary<string, string> { ["marker"] = marker },
        };

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private sealed record SaveCall(
        PenumbraDocument Document,
        string Path,
        long Generation,
        PageSaveKind Kind,
        CancellationToken CancellationToken);

    private sealed class ControlledPageStore : IPageStore
    {
        private readonly Func<SaveCall, Task<PageSaveResult>> _handler;
        private readonly object _gate = new();
        private readonly List<SaveCall> _calls = new();
        private int _concurrentCalls;
        private int _maxConcurrentCalls;

        public ControlledPageStore(Func<SaveCall, Task<PageSaveResult>>? handler = null)
        {
            _handler = handler ?? (call => Task.FromResult(
                new PageSaveResult(PageSaveStatus.Committed, call.Generation)));
        }

        public IReadOnlyList<SaveCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

        public async Task<PageSaveResult> SaveAsync(
            PenumbraDocument document,
            string path,
            long generation,
            PageSaveKind kind,
            CancellationToken cancellationToken = default)
        {
            var call = new SaveCall(document, path, generation, kind, cancellationToken);
            lock (_gate)
            {
                _calls.Add(call);
            }

            int concurrent = Interlocked.Increment(ref _concurrentCalls);
            int observed;
            while (concurrent > (observed = Volatile.Read(ref _maxConcurrentCalls)))
            {
                if (Interlocked.CompareExchange(ref _maxConcurrentCalls, concurrent, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                return await _handler(call);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        public Task<PageOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new FakeTimer(callback, state)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime,
            };
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target = _now + by;
            while (true)
            {
                FakeTimer? next = _timers
                    .Where(timer => !timer.Disposed && timer.Due is not null && timer.Due <= target)
                    .OrderBy(timer => timer.Due)
                    .FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                _now = next.Due!.Value;
                next.Due = null;
                next.Fire();
            }

            _now = target;
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public FakeTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset? Due { get; set; }
            public bool Disposed { get; private set; }

            public void Fire() => _callback(_state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
