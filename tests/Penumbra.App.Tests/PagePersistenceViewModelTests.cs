using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.App.Tests;

public sealed class PagePersistenceViewModelTests
{
    [Fact]
    public async Task DocumentChangeSchedulesImmutableV4RecoverySnapshotAfterQuietPeriod()
    {
        var time = new FakeTimeProvider();
        var store = new RecordingPageStore();
        using MainWindowViewModel vm = Create(store, time);
        vm.LiveRecognition = false;
        Stroke stroke = NewStroke();

        vm.Document.AddStroke(stroke);
        Assert.True(vm.IsDirty);
        Assert.Contains("Unsaved", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
        time.Advance(MainWindowViewModel.AutosaveQuietPeriod - TimeSpan.FromMilliseconds(1));
        Assert.Empty(store.SaveCalls);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await EventuallyAsync(() => store.SaveCalls.Count == 1);
        await EventuallyAsync(() =>
            vm.RecoveryCheckpointStatus.Contains("checkpoint saved", StringComparison.OrdinalIgnoreCase));

        SaveCall call = Assert.Single(store.SaveCalls);
        Assert.Equal(PageSaveKind.Autosave, call.Kind);
        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, call.Document.Version);
        Assert.Equal(RecognitionPipelineFingerprint.Current, call.Document.RecognitionPipelineFingerprint);
        Assert.Equal(stroke.Id, Assert.Single(call.Document.Strokes).Id);
        Assert.Equal(
            new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk),
            Assert.Single(call.Document.StrokeMetadata));
        Assert.Contains("checkpoint saved", vm.RecoveryCheckpointStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackgroundAutosaveFailureIsVisibleAndLeavesExplicitSaveAvailable()
    {
        var time = new FakeTimeProvider();
        var store = new RecordingPageStore
        {
            SaveHandler = _ => throw new IOException("injected background failure"),
        };
        using MainWindowViewModel vm = Create(store, time);
        vm.LiveRecognition = false;

        vm.Document.AddStroke(NewStroke());
        time.Advance(MainWindowViewModel.AutosaveQuietPeriod);
        await EventuallyAsync(() =>
            vm.RecoveryCheckpointStatus.Contains("checkpoint failed", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("explicit Save", vm.RecoveryCheckpointStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitSaveUsesPageStoreAndOwnsTheCurrentPath()
    {
        var store = new RecordingPageStore();
        string path = Path.Combine(Path.GetTempPath(), $"penumbra-explicit-{Guid.NewGuid():N}.pen");
        using MainWindowViewModel vm = Create(store, new FakeTimeProvider());
        vm.LiveRecognition = false;
        vm.Document.AddStroke(NewStroke());
        var callerContext = new SynchronizationContext();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task save;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            save = vm.SavePageAsync(path);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await save;

        SaveCall call = Assert.Single(store.SaveCalls, call => call.Kind == PageSaveKind.Explicit);
        Assert.Equal(Path.GetFullPath(path), call.Path);
        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, call.Document.Version);
        Assert.Equal(RecognitionPipelineFingerprint.Current, call.Document.RecognitionPipelineFingerprint);
        Assert.Equal(Path.GetFullPath(path), vm.CurrentPath);
        Assert.Contains("Saved", vm.PersistenceStatus, StringComparison.Ordinal);
        Assert.NotSame(callerContext, store.LastSaveSynchronizationContext);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task InternalRecoveryFilesCannotBecomeExplicitSaveOrOpenTargets()
    {
        string recoveryPath = Path.Combine(
            Path.GetTempPath(),
            $"penumbra-reserved-{Guid.NewGuid():N}.pen");
        var store = new RecordingPageStore();
        using MainWindowViewModel vm = Create(store, new FakeTimeProvider(), recoveryPath);

        foreach (string reservedPath in new[]
                 {
                     recoveryPath,
                     FileSystemPageStore.GetBackupPath(recoveryPath),
                 })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SavePageAsync(reservedPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.OpenPageAsync(reservedPath));
        }

        Assert.Empty(store.SaveCalls);
        Assert.Equal(0, store.OpenCallCount);
        Assert.Contains("internal recovery", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupCandidateLoadsRawInkWithoutSilentlyPromotingIt()
    {
        PenumbraDocument recovered = DocumentWithUserStroke(out Stroke stroke);
        var store = new RecordingPageStore
        {
            OpenResult = new PageOpenResult(PageOpenStatus.BackupRecoveryCandidate, recovered),
        };
        var time = new FakeTimeProvider();
        string path = Path.Combine(Path.GetTempPath(), $"penumbra-damaged-{Guid.NewGuid():N}.pen");
        using MainWindowViewModel vm = Create(store, time);
        var callerContext = new SynchronizationContext();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task<PageOpenResult> open;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            open = vm.OpenPageAsync(path);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        PageOpenResult result = await open;

        Assert.Equal(PageOpenStatus.BackupRecoveryCandidate, result.Status);
        Assert.Equal(stroke.Id, Assert.Single(vm.Document.Strokes).Id);
        Assert.Equal(Path.GetFullPath(path), vm.CurrentPath);
        Assert.Empty(store.SaveCalls); // recovery is not promoted into the damaged path
        Assert.Contains("last-known-good", vm.PersistenceStatus, StringComparison.Ordinal);
        Assert.NotSame(callerContext, store.LastOpenSynchronizationContext);

        time.Advance(MainWindowViewModel.AutosaveQuietPeriod);
        await EventuallyAsync(() =>
            vm.RecoveryCheckpointStatus.Contains("checkpoint saved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("last-known-good", vm.PersistenceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupRecoveryLoadsValidatedCheckpointAsUnsavedPage()
    {
        PenumbraDocument recovered = DocumentWithUserStroke(out Stroke stroke);
        var store = new RecordingPageStore
        {
            OpenResult = new PageOpenResult(PageOpenStatus.Current, recovered),
        };
        var time = new FakeTimeProvider();
        using MainWindowViewModel vm = Create(store, time);

        PageOpenResult? result = await vm.RecoverInterruptedSessionAsync();

        Assert.Equal(PageOpenStatus.Current, result?.Status);
        Assert.Equal(stroke.Id, Assert.Single(vm.Document.Strokes).Id);
        Assert.Null(vm.CurrentPath);
        Assert.True(vm.IsDirty);
        Assert.Contains("interrupted", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);

        time.Advance(MainWindowViewModel.AutosaveQuietPeriod);
        await EventuallyAsync(() =>
            vm.RecoveryCheckpointStatus.Contains("checkpoint saved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Choose Save", vm.PersistenceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrecoverableOpenLeavesCurrentCanvasUntouched()
    {
        var store = new RecordingPageStore
        {
            OpenResult = new PageOpenResult(PageOpenStatus.Unrecoverable, null),
        };
        using MainWindowViewModel vm = Create(store, new FakeTimeProvider());
        vm.LiveRecognition = false;
        Stroke existing = NewStroke();
        vm.Document.AddStroke(existing);
        await vm.SavePageAsync("existing.pen");

        PageOpenResult result = await vm.OpenPageAsync("unrecoverable.pen");

        Assert.Equal(PageOpenStatus.Unrecoverable, result.Status);
        Assert.Same(existing, Assert.Single(vm.Document.Strokes));
        Assert.Contains("nothing was opened", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyPageMustBeSavedBeforeOpenCanDisplaceIt()
    {
        PenumbraDocument replacement = DocumentWithUserStroke(out _);
        var store = new RecordingPageStore
        {
            OpenResult = new PageOpenResult(PageOpenStatus.Current, replacement),
        };
        using MainWindowViewModel vm = Create(store, new FakeTimeProvider());
        vm.LiveRecognition = false;
        Stroke unsaved = NewStroke();
        vm.Document.AddStroke(unsaved);

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.OpenPageAsync("replacement.pen"));

        Assert.True(vm.IsDirty);
        Assert.Same(unsaved, Assert.Single(vm.Document.Strokes));
        Assert.Equal(0, store.OpenCallCount);
        Assert.Contains("Save", vm.PersistenceStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentPageOperationIsRefusedBeforeItCanSnapshotOrWrite()
    {
        var openEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new RecordingPageStore
        {
            OpenHandler = async (_, _) =>
            {
                openEntered.TrySetResult();
                await releaseOpen.Task;
                return new PageOpenResult(PageOpenStatus.NotFound, null);
            },
        };
        using MainWindowViewModel vm = Create(store, new FakeTimeProvider());
        Task<PageOpenResult> open = vm.OpenPageAsync("blocked-open.pen");
        await openEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.SavePageAsync("must-not-write.pen"));

        Assert.Empty(store.SaveCalls);
        Assert.Contains("was not started", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
        releaseOpen.TrySetResult();
        await open;
    }

    [Fact]
    public async Task RecognitionFailureAfterOpenStillPreservesValidatedRawInk()
    {
        PenumbraDocument recovered = DocumentWithUserStroke(out Stroke stroke);
        var store = new RecordingPageStore
        {
            OpenResult = new PageOpenResult(PageOpenStatus.Current, recovered),
        };
        var time = new FakeTimeProvider();
        using MainWindowViewModel vm = Create(
            store,
            time,
            recognizer: new ThrowingRecognizer());

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.OpenPageAsync("valid.pen"));

        Assert.Equal(stroke.Id, Assert.Single(vm.Document.Strokes).Id);
        Assert.Equal(StrokeOriginKind.UserInk, vm.Document.GetStrokeOrigin(stroke.Id));
        Assert.Contains("raw ink", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preserved", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);

        time.Advance(MainWindowViewModel.AutosaveQuietPeriod);
        await EventuallyAsync(() =>
            vm.RecoveryCheckpointStatus.Contains("checkpoint saved", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("raw ink", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirtyCleanShutdownFlushesLatestAndRetainsRecoveryArtifacts()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-close-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string recoveryPath = Path.Combine(directory, "interrupted-session.pen");
        string backupPath = FileSystemPageStore.GetBackupPath(recoveryPath);
        await File.WriteAllTextAsync(recoveryPath, "checkpoint");
        await File.WriteAllTextAsync(backupPath, "backup");
        var store = new RecordingPageStore();
        var time = new FakeTimeProvider();

        try
        {
            using MainWindowViewModel vm = Create(store, time, recoveryPath);
            vm.LiveRecognition = false;
            vm.Document.AddStroke(NewStroke());

            await vm.CompleteCleanShutdownAsync();

            Assert.Single(store.SaveCalls, call => call.Kind == PageSaveKind.Autosave);
            Assert.True(vm.IsDirty);
            Assert.True(File.Exists(recoveryPath));
            Assert.True(File.Exists(backupPath));
            Assert.Contains("retained", vm.RecoveryCheckpointStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DurablySavedCleanShutdownFlushesThenDeletesRecoveryArtifacts()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-close-saved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string recoveryPath = Path.Combine(directory, "interrupted-session.pen");
        string backupPath = FileSystemPageStore.GetBackupPath(recoveryPath);
        string pagePath = Path.Combine(directory, "saved-page.pen");
        await File.WriteAllTextAsync(recoveryPath, "checkpoint");
        await File.WriteAllTextAsync(backupPath, "backup");
        var store = new RecordingPageStore();

        try
        {
            using MainWindowViewModel vm = Create(store, new FakeTimeProvider(), recoveryPath);
            vm.LiveRecognition = false;
            vm.Document.AddStroke(NewStroke());
            await vm.SavePageAsync(pagePath);
            Assert.False(vm.IsDirty);

            await vm.CompleteCleanShutdownAsync();

            Assert.Single(store.SaveCalls, call => call.Kind == PageSaveKind.Explicit);
            Assert.Single(store.SaveCalls, call => call.Kind == PageSaveKind.Autosave);
            Assert.False(File.Exists(recoveryPath));
            Assert.False(File.Exists(backupPath));
            Assert.Contains("clean close", vm.RecoveryCheckpointStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedCloseFlushKeepsRecoveryArtifactAndSurfacesFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-close-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string recoveryPath = Path.Combine(directory, "interrupted-session.pen");
        await File.WriteAllTextAsync(recoveryPath, "checkpoint");
        var store = new RecordingPageStore
        {
            SaveHandler = _ => throw new IOException("injected close failure"),
        };

        try
        {
            using MainWindowViewModel vm = Create(store, new FakeTimeProvider(), recoveryPath);
            vm.LiveRecognition = false;
            vm.Document.AddStroke(NewStroke());

            await Assert.ThrowsAsync<IOException>(() => vm.CompleteCleanShutdownAsync());

            Assert.True(File.Exists(recoveryPath));
            Assert.Contains("window remains open", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedStartupInspectionCannotDeleteAnUnverifiedRecoveryArtifactOnClose()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-inspection-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string recoveryPath = Path.Combine(directory, "interrupted-session.pen");
        await File.WriteAllTextAsync(recoveryPath, "unverified checkpoint");
        var store = new RecordingPageStore
        {
            OpenHandler = (_, _) => throw new IOException("injected read failure"),
        };

        try
        {
            using MainWindowViewModel vm = Create(store, new FakeTimeProvider(), recoveryPath);
            await Assert.ThrowsAsync<IOException>(() => vm.RecoverInterruptedSessionAsync());

            await vm.CompleteCleanShutdownAsync();

            Assert.True(File.Exists(recoveryPath));
            Assert.Contains("Unverified", vm.RecoveryCheckpointStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BackupCleanupFailureLeavesNewestRecoveryCurrentIntact()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-cleanup-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string recoveryPath = Path.Combine(directory, "interrupted-session.pen");
        string backupPath = FileSystemPageStore.GetBackupPath(recoveryPath);
        await File.WriteAllTextAsync(recoveryPath, "newest checkpoint");
        Directory.CreateDirectory(backupPath); // File.Delete deterministically refuses a directory.

        try
        {
            using MainWindowViewModel vm = Create(
                new RecordingPageStore(),
                new FakeTimeProvider(),
                recoveryPath);
            vm.LiveRecognition = false;
            vm.Document.AddStroke(NewStroke());
            await vm.SavePageAsync(Path.Combine(directory, "saved-page.pen"));
            Assert.False(vm.IsDirty);

            await Assert.ThrowsAnyAsync<Exception>(() => vm.CompleteCleanShutdownAsync());

            Assert.True(File.Exists(recoveryPath));
            Assert.Contains("kept", vm.PersistenceStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealFileStoreSaveAndOpenRoundTripsThroughViewModelSeam()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-vm-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string pagePath = Path.Combine(directory, "page.pen");
        string sourceRecovery = Path.Combine(directory, "source-recovery.pen");
        string targetRecovery = Path.Combine(directory, "target-recovery.pen");
        var store = new FileSystemPageStore();
        Stroke stroke = NewStroke();

        try
        {
            using (MainWindowViewModel source = Create(store, new FakeTimeProvider(), sourceRecovery))
            {
                source.LiveRecognition = false;
                source.Document.AddStroke(stroke);
                await source.SavePageAsync(pagePath);
            }

            using MainWindowViewModel target = Create(store, new FakeTimeProvider(), targetRecovery);
            PageOpenResult result = await target.OpenPageAsync(pagePath);

            Assert.Equal(PageOpenStatus.Current, result.Status);
            Assert.Equal(stroke.Id, Assert.Single(target.Document.Strokes).Id);
            Assert.Equal(StrokeOriginKind.UserInk, target.Document.GetStrokeOrigin(stroke.Id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealFileStoreRetainsDirtyCleanCloseForNextStartupRecovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"penumbra-session-retain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string recoveryPath = Path.Combine(directory, "interrupted-session.pen");
        var store = new FileSystemPageStore();
        Stroke stroke = NewStroke();

        try
        {
            using (MainWindowViewModel source = Create(store, new FakeTimeProvider(), recoveryPath))
            {
                source.LiveRecognition = false;
                source.Document.AddStroke(stroke);
                await source.CompleteCleanShutdownAsync();
                Assert.True(File.Exists(recoveryPath));
                Assert.True(source.IsDirty);
            }

            using MainWindowViewModel recovered = Create(store, new FakeTimeProvider(), recoveryPath);
            PageOpenResult? result = await recovered.RecoverInterruptedSessionAsync();

            Assert.Equal(PageOpenStatus.Current, result?.Status);
            Assert.Equal(stroke.Id, Assert.Single(recovered.Document.Strokes).Id);
            Assert.True(recovered.IsDirty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MainWindowViewModel Create(
        IPageStore store,
        TimeProvider time,
        string? recoveryPath = null,
        IRegionRecognizer? recognizer = null) => new(
        recognizer ?? new EmptyRecognizer(),
        new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
        time,
        store,
        recoveryPath ?? Path.Combine(
            Path.GetTempPath(),
            $"penumbra-recovery-{Guid.NewGuid():N}.pen"),
        static action => action());

    private static PenumbraDocument DocumentWithUserStroke(out Stroke stroke)
    {
        stroke = NewStroke();
        var ink = new InkDocument();
        ink.AddStroke(stroke);
        return ink.ToDocument() with
        {
            RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
        };
    }

    private static Stroke NewStroke() => new(
        Guid.NewGuid(),
        new[]
        {
            new StrokeSample(1, 2, TimeSpan.Zero, .5),
            new StrokeSample(3, 4, TimeSpan.FromMilliseconds(10), .6),
        });

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

    private sealed class RecordingPageStore : IPageStore
    {
        private readonly object _gate = new();
        private readonly List<SaveCall> _saveCalls = new();

        public Func<SaveCall, Task<PageSaveResult>>? SaveHandler { get; init; }
        public Func<string, CancellationToken, Task<PageOpenResult>>? OpenHandler { get; init; }
        public PageOpenResult OpenResult { get; init; } =
            new(PageOpenStatus.NotFound, null);
        private SynchronizationContext? _lastSaveSynchronizationContext;
        private SynchronizationContext? _lastOpenSynchronizationContext;
        private int _openCallCount;

        public SynchronizationContext? LastSaveSynchronizationContext =>
            Volatile.Read(ref _lastSaveSynchronizationContext);
        public SynchronizationContext? LastOpenSynchronizationContext =>
            Volatile.Read(ref _lastOpenSynchronizationContext);
        public int OpenCallCount => Volatile.Read(ref _openCallCount);

        public IReadOnlyList<SaveCall> SaveCalls
        {
            get
            {
                lock (_gate)
                {
                    return _saveCalls.ToArray();
                }
            }
        }

        public async Task<PageSaveResult> SaveAsync(
            PenumbraDocument document,
            string path,
            long generation,
            PageSaveKind kind,
            CancellationToken cancellationToken = default)
        {
            Volatile.Write(ref _lastSaveSynchronizationContext, SynchronizationContext.Current);
            var call = new SaveCall(document, path, generation, kind, cancellationToken);
            lock (_gate)
            {
                _saveCalls.Add(call);
            }

            return SaveHandler is null
                ? new PageSaveResult(PageSaveStatus.Committed, generation)
                : await SaveHandler(call);
        }

        public Task<PageOpenResult> OpenAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _openCallCount);
            Volatile.Write(ref _lastOpenSynchronizationContext, SynchronizationContext.Current);
            return OpenHandler is null
                ? Task.FromResult(OpenResult)
                : OpenHandler(path, cancellationToken);
        }
    }

    private sealed class EmptyRecognizer : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Array.Empty<RegionRecognition>();

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RegionRecognition>>(Array.Empty<RegionRecognition>());
    }

    private sealed class ThrowingRecognizer : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("injected");

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<RegionRecognition>>(new InvalidOperationException("injected"));
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
