namespace Penumbra.Core;

/// <summary>
/// Debounces immutable page snapshots and serializes latest-write-wins autosaves through an
/// <see cref="IPageStore"/>. Snapshot creation remains the caller's responsibility, so UI-owned
/// document state is never read from a timer or I/O thread.
/// </summary>
public sealed class PageAutosaveCoordinator : IAsyncDisposable
{
    private readonly IPageStore _pageStore;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalMetricsSink _metricsSink;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<Task> _backgroundTasks = new();

    private PendingSave? _latest;
    private CancellationTokenSource? _delayCancellation;
    private CancellationTokenSource? _activeWriteCancellation;
    private long _latestRevision;
    private long _committedRevision;
    private Exception? _lastFailure;
    private bool _disposed;

    /// <summary>
    /// Queued asynchronously when a revision commits or the latest background revision fails. Delivery
    /// may occur after a newer revision is scheduled; UI subscribers must marshal and filter accordingly.
    /// Slow, reentrant, or failing handlers never control the persistence worker.
    /// </summary>
    public event EventHandler<PageAutosaveStateChangedEventArgs>? StateChanged;

    public PageAutosaveCoordinator(
        IPageStore pageStore,
        TimeSpan quietPeriod,
        TimeProvider? timeProvider = null,
        ILocalMetricsSink? metricsSink = null)
    {
        ArgumentNullException.ThrowIfNull(pageStore);
        if (quietPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quietPeriod),
                quietPeriod,
                "quiet period must be positive");
        }

        _pageStore = pageStore;
        _quietPeriod = quietPeriod;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metricsSink = metricsSink ?? NoOpLocalMetricsSink.Instance;
    }

    /// <summary>The newest revision supplied to <see cref="Schedule"/>.</summary>
    public long LatestRevision
    {
        get
        {
            lock (_gate)
            {
                return _latestRevision;
            }
        }
    }

    /// <summary>The newest revision durably committed by this coordinator.</summary>
    public long CommittedRevision
    {
        get
        {
            lock (_gate)
            {
                return _committedRevision;
            }
        }
    }

    /// <summary>
    /// The latest background autosave failure, cleared when a newer snapshot is scheduled or that
    /// revision is later committed. Failures are retained for an explicit flush instead of becoming
    /// unobserved task exceptions.
    /// </summary>
    public Exception? LastFailure
    {
        get
        {
            lock (_gate)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>
    /// Schedules an already-immutable snapshot and returns its monotonically increasing revision.
    /// Re-signalling restarts the quiet period and requests cancellation of any older in-flight save.
    /// </summary>
    public long Schedule(PenumbraDocument snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        CancellationTokenSource? previousDelay;
        CancellationTokenSource? previousWrite;
        CancellationTokenSource delayCancellation;
        Task backgroundTask;
        long revision;

        lock (_gate)
        {
            ThrowIfDisposed();
            revision = checked(++_latestRevision);
            _latest = new PendingSave(revision, snapshot, path);
            _lastFailure = null;

            previousDelay = _delayCancellation;
            previousWrite = _activeWriteCancellation;
            delayCancellation = new CancellationTokenSource();
            _delayCancellation = delayCancellation;
            backgroundTask = RunAfterQuietPeriodAsync(revision, delayCancellation);
            _backgroundTasks.Add(backgroundTask);
        }

        CancelBestEffort(previousDelay);
        CancelBestEffort(previousWrite);
        ObserveBackgroundCompletion(backgroundTask);
        return revision;
    }

    /// <summary>
    /// Bypasses the debounce and waits until the latest revision is durable. If a newer revision is
    /// scheduled during the flush, the flush follows it rather than returning after an older commit.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        using MetricTimingScope timing = MetricTimingScope.Start(
            _metricsSink,
            MetricOperation.CloseFlush,
            _timeProvider);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long revision;
                CancellationTokenSource? delayToCancel;
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (_latest is null || _committedRevision >= _latest.Revision)
                    {
                        timing.Complete();
                        return;
                    }

                    revision = _latest.Revision;
                    delayToCancel = _delayCancellation;
                }

                CancelBestEffort(delayToCancel);

                try
                {
                    await SaveRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A newer Schedule cancelled the old write. Loop and flush that newer revision.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            timing.Cancel();
            throw;
        }
        catch
        {
            timing.Fail();
            throw;
        }
    }

    private async Task RunAfterQuietPeriodAsync(
        long revision,
        CancellationTokenSource delayCancellation)
    {
        try
        {
            await Task.Delay(_quietPeriod, _timeProvider, delayCancellation.Token).ConfigureAwait(false);
            await SaveRevisionAsync(revision, delayCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
        {
            // Supersession/disposal is an expected terminal path for background work.
        }
        catch (Exception error)
        {
            bool retainedFailure = false;
            lock (_gate)
            {
                if (!_disposed && _latest?.Revision == revision && _committedRevision < revision)
                {
                    _lastFailure = error;
                    retainedFailure = true;
                }
            }

            if (retainedFailure)
            {
                QueueStateChanged(new PageAutosaveStateChangedEventArgs(
                    revision,
                    committed: false,
                    failure: error));
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_delayCancellation, delayCancellation))
                {
                    _delayCancellation = null;
                }
            }

            delayCancellation.Dispose();
        }
    }

    private async Task SaveRevisionAsync(long revision, CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? writeCancellation = null;
        try
        {
            PendingSave pending;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_latest is null || _latest.Revision != revision || _committedRevision >= revision)
                {
                    return;
                }

                pending = _latest;
                writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeWriteCancellation = writeCancellation;
            }

            // Invoke the complete store operation on a worker. Some IPageStore implementations perform
            // serialization or filesystem setup before their first incomplete await; FlushAsync is allowed
            // to originate on a UI thread, so merely using ConfigureAwait(false) is insufficient.
            PageSaveResult result = await Task.Run(
                () => _pageStore.SaveAsync(
                    pending.Document,
                    pending.Path,
                    pending.Revision,
                    PageSaveKind.Autosave,
                    writeCancellation.Token),
                writeCancellation.Token).ConfigureAwait(false);

            if (result.Status != PageSaveStatus.Committed || result.Generation != pending.Revision)
            {
                throw new IOException(
                    $"Autosave revision {pending.Revision} was superseded before it became durable.");
            }

            lock (_gate)
            {
                _committedRevision = Math.Max(_committedRevision, pending.Revision);
                if (_latest?.Revision == pending.Revision)
                {
                    _lastFailure = null;
                }
            }

            QueueStateChanged(new PageAutosaveStateChangedEventArgs(
                pending.Revision,
                committed: true,
                failure: null));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeWriteCancellation, writeCancellation))
                {
                    _activeWriteCancellation = null;
                }
            }

            writeCancellation?.Dispose();
            _writerGate.Release();
        }
    }

    private void ObserveBackgroundCompletion(Task task)
    {
        _ = task.ContinueWith(
            completed =>
            {
                lock (_gate)
                {
                    _backgroundTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void QueueStateChanged(PageAutosaveStateChangedEventArgs args)
    {
        // Do not await observer code. A handler may synchronously dispose this coordinator; running it
        // inline from a tracked background task would let it deadlock on its own completion.
        _ = Task.Run(() => NotifyStateChanged(args));
    }

    private void NotifyStateChanged(PageAutosaveStateChangedEventArgs args)
    {
        EventHandler<PageAutosaveStateChangedEventArgs>? handlers = StateChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<PageAutosaveStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // Persistence authority cannot depend on a diagnostics/UI observer.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PageAutosaveCoordinator));
        }
    }

    private static void CancelBestEffort(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning asynchronous path won the race and already reached its terminal cleanup.
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? delayToCancel;
        CancellationTokenSource? writeToCancel;
        Task[] backgroundTasks;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            delayToCancel = _delayCancellation;
            writeToCancel = _activeWriteCancellation;
            backgroundTasks = _backgroundTasks.ToArray();
        }

        CancelBestEffort(delayToCancel);
        CancelBestEffort(writeToCancel);
        await Task.WhenAll(backgroundTasks).ConfigureAwait(false);

        // A caller may already be inside FlushAsync rather than a tracked background task. Crossing
        // the same writer gate proves that every active store call observed cancellation and exited.
        await _writerGate.WaitAsync().ConfigureAwait(false);
        _writerGate.Release();
    }

    private sealed record PendingSave(long Revision, PenumbraDocument Document, string Path);
}

/// <summary>One observable terminal state for an autosave revision.</summary>
public sealed class PageAutosaveStateChangedEventArgs : EventArgs
{
    public PageAutosaveStateChangedEventArgs(long revision, bool committed, Exception? failure)
    {
        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "revision must be positive");
        }

        if (committed == (failure is not null))
        {
            throw new ArgumentException(
                "Committed state requires no failure; failed state requires one failure.",
                nameof(failure));
        }

        Revision = revision;
        Committed = committed;
        Failure = failure;
    }

    public long Revision { get; }
    public bool Committed { get; }
    public Exception? Failure { get; }
}
