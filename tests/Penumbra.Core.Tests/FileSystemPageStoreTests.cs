using Penumbra.Core;

namespace Penumbra.Core.Tests;

public sealed class FileSystemPageStoreTests
{
    [Fact]
    public async Task FirstSave_CommitsCurrentWithoutCreatingBackup()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var metrics = new BoundedInMemoryMetricsSink(8);
        var store = new FileSystemPageStore(metrics);

        PageSaveResult result = await store.SaveAsync(
            Document("first"),
            path,
            generation: 1,
            PageSaveKind.Explicit);

        Assert.Equal(new PageSaveResult(PageSaveStatus.Committed, 1), result);
        Assert.Equal("first", await ReadMarkerAsync(path));
        Assert.False(File.Exists(FileSystemPageStore.GetBackupPath(path)));
        Assert.Empty(directory.TemporaryFiles());
        AssertMetric(metrics, MetricOperation.ExplicitSave, MetricOutcome.Completed);
    }

    [Fact]
    public async Task RepeatedSave_CommitsNewCurrentAndRetainsPreviousValidCurrentAsBackup()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var metrics = new BoundedInMemoryMetricsSink(8);
        var store = new FileSystemPageStore(metrics);

        await store.SaveAsync(Document("first"), path, 1, PageSaveKind.Explicit);
        PageSaveResult secondResult = await store.SaveAsync(Document("second"), path, 2, PageSaveKind.Autosave);

        Assert.Equal(PageSaveStatus.Committed, secondResult.Status);
        Assert.Equal("second", await ReadMarkerAsync(path));
        Assert.Equal("first", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));

        PageSaveResult result = await store.SaveAsync(Document("third"), path, 3, PageSaveKind.Autosave);

        Assert.Equal(PageSaveStatus.Committed, result.Status);
        Assert.Equal("third", await ReadMarkerAsync(path));
        Assert.Equal("second", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
        Assert.Empty(directory.TemporaryFiles());
        Assert.Equal(
            new[]
            {
                (MetricOperation.ExplicitSave, MetricOutcome.Completed),
                (MetricOperation.Autosave, MetricOutcome.Completed),
                (MetricOperation.Autosave, MetricOutcome.Completed),
            },
            metrics.Snapshot().Observations
                .Select(observation => (observation.Operation, observation.Outcome)));
    }

    [Fact]
    public async Task ReplaceFallback_CommitsNewCurrentAndPublishesValidatedBackupFirst()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var injector = new TestFaultInjector { ForceReplaceFallback = true };
        var store = new FileSystemPageStore(
            NoOpLocalMetricsSink.Instance,
            TimeProvider.System,
            injector);

        await store.SaveAsync(Document("first"), path, 1, PageSaveKind.Explicit);
        await store.SaveAsync(Document("second"), path, 2, PageSaveKind.Autosave);

        Assert.Equal("second", await ReadMarkerAsync(path));
        Assert.Equal("first", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
        Assert.Empty(directory.TemporaryFiles());
    }

    [Fact]
    public async Task SaveOverCorruptCurrent_PreservesExistingValidBackup()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var seedStore = new FileSystemPageStore();
        await seedStore.SaveAsync(Document("first"), path, 1, PageSaveKind.Explicit);
        await seedStore.SaveAsync(Document("second"), path, 2, PageSaveKind.Autosave);
        await File.WriteAllTextAsync(path, "not-json");

        var store = new FileSystemPageStore();
        await store.SaveAsync(Document("replacement"), path, 3, PageSaveKind.Explicit);

        Assert.Equal("replacement", await ReadMarkerAsync(path));
        Assert.Equal("first", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
    }

    [Fact]
    public async Task CancellationAtCommitBoundary_PreservesCurrentAndBackup()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var seedStore = new FileSystemPageStore();
        await seedStore.SaveAsync(Document("first"), path, 1, PageSaveKind.Explicit);
        await seedStore.SaveAsync(Document("second"), path, 2, PageSaveKind.Autosave);

        var metrics = new BoundedInMemoryMetricsSink(8);
        var injector = new BlockingCommitInjector(generationToBlock: 3);
        var store = new FileSystemPageStore(metrics, TimeProvider.System, injector);
        using var cancellation = new CancellationTokenSource();

        Task<PageSaveResult> pending = store.SaveAsync(
            Document("cancelled"),
            path,
            3,
            PageSaveKind.Autosave,
            cancellation.Token);
        await injector.WaitUntilBlockedAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("second", await ReadMarkerAsync(path));
        Assert.Equal("first", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
        Assert.Empty(directory.TemporaryFiles());
        AssertMetric(metrics, MetricOperation.Autosave, MetricOutcome.Cancelled);
    }

    [Fact]
    public async Task NewerGenerationAtCommitBoundary_RefusesStaleWriteAndCommitsLatest()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var metrics = new BoundedInMemoryMetricsSink(8);
        var injector = new BlockingCommitInjector(generationToBlock: 1);
        var store = new FileSystemPageStore(metrics, TimeProvider.System, injector);

        Task<PageSaveResult> stale = store.SaveAsync(
            Document("stale"),
            path,
            1,
            PageSaveKind.Autosave);
        await injector.WaitUntilBlockedAsync();
        Task<PageSaveResult> latest = store.SaveAsync(
            Document("latest"),
            path,
            2,
            PageSaveKind.Autosave);
        injector.Release();

        PageSaveResult[] results = await Task.WhenAll(stale, latest);

        Assert.Equal(PageSaveStatus.Superseded, results[0].Status);
        Assert.Equal(PageSaveStatus.Committed, results[1].Status);
        Assert.Equal("latest", await ReadMarkerAsync(path));
        Assert.False(File.Exists(FileSystemPageStore.GetBackupPath(path)));
        Assert.Empty(directory.TemporaryFiles());
        Assert.Equal(
            new[] { MetricOutcome.Refused, MetricOutcome.Completed },
            metrics.Snapshot().Observations.Select(observation => observation.Outcome));
    }

    [Fact]
    public async Task ConcurrentStoreInstances_NewerGenerationCannotBeOverwrittenByStaleWriter()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        await new FileSystemPageStore().SaveAsync(Document("seed"), path, 0, PageSaveKind.Explicit);

        var staleInjector = new BlockingCommitInjector(generationToBlock: 1);
        var staleStore = new FileSystemPageStore(
            NoOpLocalMetricsSink.Instance,
            TimeProvider.System,
            staleInjector);
        var latestStore = new FileSystemPageStore();
        string latestPath = OperatingSystem.IsWindows()
            ? path.ToUpperInvariant()
            : Path.GetRelativePath(Environment.CurrentDirectory, path);

        Task<PageSaveResult> stale = staleStore.SaveAsync(
            Document("stale"),
            path,
            1,
            PageSaveKind.Autosave);
        await staleInjector.WaitUntilBlockedAsync();

        Task<PageSaveResult> latest = latestStore.SaveAsync(
            Document("latest"),
            latestPath,
            2,
            PageSaveKind.Autosave);
        staleInjector.Release();
        PageSaveResult[] results = await Task.WhenAll(stale, latest);

        Assert.Equal(PageSaveStatus.Superseded, results[0].Status);
        Assert.Equal(PageSaveStatus.Committed, results[1].Status);
        Assert.Equal("latest", await ReadMarkerAsync(path));
    }

    [Fact]
    public async Task QuiescentWriter_CanBeginANewGenerationEpoch()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var store = new FileSystemPageStore();
        await store.SaveAsync(Document("previous-session"), path, 10, PageSaveKind.Autosave);

        PageSaveResult result = await store.SaveAsync(
            Document("next-session"),
            path,
            1,
            PageSaveKind.Autosave);

        Assert.Equal(PageSaveStatus.Committed, result.Status);
        Assert.Equal("next-session", await ReadMarkerAsync(path));
        Assert.Equal("previous-session", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
    }

    [Fact]
    public async Task FailedTemporaryValidation_NeverChangesCurrentOrBackup()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var seedStore = new FileSystemPageStore();
        await seedStore.SaveAsync(Document("first"), path, 1, PageSaveKind.Explicit);
        await seedStore.SaveAsync(Document("second"), path, 2, PageSaveKind.Autosave);

        var metrics = new BoundedInMemoryMetricsSink(8);
        var injector = new TestFaultInjector
        {
            AfterTemporaryFileFlushed = async (temporaryPath, _, cancellationToken) =>
                await File.WriteAllTextAsync(
                    temporaryPath,
                    PenumbraDocumentSerializer.Serialize(Document("different-but-valid")),
                    cancellationToken),
        };
        var store = new FileSystemPageStore(metrics, TimeProvider.System, injector);

        await Assert.ThrowsAsync<PageStoreValidationException>(() => store.SaveAsync(
            Document("must-not-commit"),
            path,
            3,
            PageSaveKind.Autosave));

        Assert.Equal("second", await ReadMarkerAsync(path));
        Assert.Equal("first", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
        Assert.Empty(directory.TemporaryFiles());
        AssertMetric(metrics, MetricOperation.Autosave, MetricOutcome.Failed);
    }

    [Fact]
    public async Task ForeignCancellationException_IsFailedRatherThanCallerCancelled()
    {
        using var directory = new TemporaryDirectory();
        var metrics = new BoundedInMemoryMetricsSink(8);
        var injector = new TestFaultInjector
        {
            BeforeCommit = (_, _, _, _) => ValueTask.FromException(
                new OperationCanceledException(new CancellationToken(canceled: true))),
        };
        var store = new FileSystemPageStore(metrics, TimeProvider.System, injector);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(
            Document("never"),
            directory.PagePath,
            1,
            PageSaveKind.Explicit));

        AssertMetric(metrics, MetricOperation.ExplicitSave, MetricOutcome.Failed);
    }

    [Fact]
    public async Task CancellationObservedByMetricsAfterRename_DoesNotRewriteCommittedResult()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var metrics = new CancellingMetricsSink(cancellation);
        var store = new FileSystemPageStore(metrics);

        PageSaveResult result = await store.SaveAsync(
            Document("committed"),
            directory.PagePath,
            1,
            PageSaveKind.Explicit,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(PageSaveStatus.Committed, result.Status);
        Assert.Equal("committed", await ReadMarkerAsync(directory.PagePath));
        Assert.Equal(MetricOutcome.Completed, Assert.Single(metrics.Observations).Outcome);
    }

    [Fact]
    public async Task CorruptCurrentAndValidBackup_ReturnsExplicitCandidateWithoutPromotion()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var seedStore = new FileSystemPageStore();
        await seedStore.SaveAsync(Document("backup"), path, 1, PageSaveKind.Explicit);
        await seedStore.SaveAsync(Document("current"), path, 2, PageSaveKind.Autosave);
        await File.WriteAllTextAsync(path, "not-json");

        var metrics = new BoundedInMemoryMetricsSink(8);
        var store = new FileSystemPageStore(metrics);
        PageOpenResult result = await store.OpenAsync(path);

        Assert.Equal(PageOpenStatus.BackupRecoveryCandidate, result.Status);
        Assert.Equal("backup", Marker(result.Document));
        Assert.Equal("not-json", await File.ReadAllTextAsync(path));
        AssertMetric(metrics, MetricOperation.RecoveryRead, MetricOutcome.Completed);
    }

    [Fact]
    public async Task ValidCurrent_BeatsNewerStaleTemporaryFileAndCleansIt()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var store = new FileSystemPageStore();
        await store.SaveAsync(Document("current"), path, 1, PageSaveKind.Explicit);
        string staleTemporary = directory.CreateTemporaryFile(Document("not-authoritative"));
        File.SetLastWriteTimeUtc(staleTemporary, DateTime.UtcNow - TimeSpan.FromDays(2));

        PageOpenResult result = await store.OpenAsync(path);

        Assert.Equal(PageOpenStatus.Current, result.Status);
        Assert.Equal("current", Marker(result.Document));
        Assert.False(File.Exists(staleTemporary));
    }

    [Fact]
    public async Task MissingCurrentAndValidBackup_ReturnsCandidateWithoutPromotingIt()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        string backupPath = FileSystemPageStore.GetBackupPath(path);
        var seedStore = new FileSystemPageStore();
        await seedStore.SaveAsync(Document("backup"), path, 1, PageSaveKind.Explicit);
        File.Move(path, backupPath);

        PageOpenResult result = await seedStore.OpenAsync(path);

        Assert.Equal(PageOpenStatus.BackupRecoveryCandidate, result.Status);
        Assert.Equal("backup", Marker(result.Document));
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public async Task CorruptCurrentAndBackup_ReturnsNoDocumentAndRecordsRefusal()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        await File.WriteAllTextAsync(path, "bad-current");
        await File.WriteAllTextAsync(FileSystemPageStore.GetBackupPath(path), "bad-backup");
        var metrics = new BoundedInMemoryMetricsSink(8);
        var store = new FileSystemPageStore(metrics);

        PageOpenResult result = await store.OpenAsync(path);

        Assert.Equal(PageOpenStatus.Unrecoverable, result.Status);
        Assert.Null(result.Document);
        AssertMetric(metrics, MetricOperation.RecoveryRead, MetricOutcome.Refused);
    }

    [Fact]
    public async Task MissingCurrentAndBackup_ReturnsNotFoundRatherThanCorruptData()
    {
        using var directory = new TemporaryDirectory();
        var metrics = new BoundedInMemoryMetricsSink(8);
        var store = new FileSystemPageStore(metrics);

        PageOpenResult result = await store.OpenAsync(directory.PagePath);

        Assert.Equal(PageOpenStatus.NotFound, result.Status);
        Assert.Null(result.Document);
        AssertMetric(metrics, MetricOperation.RecoveryRead, MetricOutcome.Completed);
    }

    [Fact]
    public async Task LowerGenerationDuringActiveSave_IsRefusedWithoutTouchingCurrent()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        await new FileSystemPageStore().SaveAsync(Document("seed"), path, 0, PageSaveKind.Explicit);
        var metrics = new BoundedInMemoryMetricsSink(8);
        var injector = new BlockingCommitInjector(generationToBlock: 10);
        var latestStore = new FileSystemPageStore(metrics, TimeProvider.System, injector);
        var staleStore = new FileSystemPageStore(metrics);
        Task<PageSaveResult> latest = latestStore.SaveAsync(
            Document("newest"),
            path,
            10,
            PageSaveKind.Autosave);
        await injector.WaitUntilBlockedAsync();

        PageSaveResult stale = await staleStore.SaveAsync(
            Document("older"),
            path,
            9,
            PageSaveKind.Autosave);
        injector.Release();
        PageSaveResult committed = await latest;

        Assert.Equal(PageSaveStatus.Superseded, stale.Status);
        Assert.Equal(PageSaveStatus.Committed, committed.Status);
        Assert.Equal("newest", await ReadMarkerAsync(path));
        Assert.Equal("seed", await ReadMarkerAsync(FileSystemPageStore.GetBackupPath(path)));
        Assert.Equal(
            new[] { MetricOutcome.Refused, MetricOutcome.Completed },
            metrics.Snapshot().Observations.Select(observation => observation.Outcome));
    }

    [Fact]
    public async Task CancelledRecoveryRead_RecordsCallerCancellation()
    {
        using var directory = new TemporaryDirectory();
        var metrics = new BoundedInMemoryMetricsSink(8);
        var store = new FileSystemPageStore(metrics);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.OpenAsync(directory.PagePath, cancellation.Token));

        AssertMetric(metrics, MetricOperation.RecoveryRead, MetricOutcome.Cancelled);
    }

    [Fact]
    public async Task StaleTemporaryCleanup_DeletesOnlyBoundedKnownFiles()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var store = new FileSystemPageStore();
        await store.SaveAsync(Document("current"), path, 1, PageSaveKind.Explicit);

        int created = FileSystemPageStore.MaxTemporaryFilesDeletedPerSweep + 4;
        for (int index = 0; index < created; index++)
        {
            string staleTemporary = directory.CreateTemporaryFile(Document(index.ToString()));
            File.SetLastWriteTimeUtc(staleTemporary, DateTime.UtcNow - TimeSpan.FromDays(2));
        }

        PageOpenResult result = await store.OpenAsync(path);

        Assert.Equal(PageOpenStatus.Current, result.Status);
        Assert.Equal(
            created - FileSystemPageStore.MaxTemporaryFilesDeletedPerSweep,
            directory.TemporaryFiles().Count);
    }

    [Fact]
    public async Task ForeignTemporaryFiles_CannotStarveKnownStaleCleanup()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.PagePath;
        var store = new FileSystemPageStore();
        await store.SaveAsync(Document("current"), path, 1, PageSaveKind.Explicit);
        string containingDirectory = Path.GetDirectoryName(path)!;

        for (int index = 0; index < FileSystemPageStore.MaxTemporaryFilesExaminedPerSweep; index++)
        {
            string foreign = Path.Combine(containingDirectory, $"foreign-{index:D3}.tmp");
            await File.WriteAllTextAsync(foreign, "foreign");
            File.SetLastWriteTimeUtc(foreign, DateTime.UtcNow - TimeSpan.FromDays(2));
        }

        string knownStale = directory.CreateTemporaryFile(Document("stale"));
        File.SetLastWriteTimeUtc(knownStale, DateTime.UtcNow - TimeSpan.FromDays(2));

        PageOpenResult result = await store.OpenAsync(path);

        Assert.Equal(PageOpenStatus.Current, result.Status);
        Assert.False(File.Exists(knownStale));
        Assert.Equal(
            FileSystemPageStore.MaxTemporaryFilesExaminedPerSweep,
            Directory.GetFiles(containingDirectory, "foreign-*.tmp").Length);
    }

    private static PenumbraDocument Document(string marker) =>
        PenumbraDocumentSerializer.CreateEmpty() with
        {
            Variables = new Dictionary<string, string> { ["marker"] = marker },
        };

    private static string Marker(PenumbraDocument? document)
    {
        Assert.NotNull(document);
        return document.Variables["marker"];
    }

    private static async Task<string> ReadMarkerAsync(string path) =>
        Marker(await PenumbraDocumentSerializer.LoadAsync(path));

    private static void AssertMetric(
        BoundedInMemoryMetricsSink metrics,
        MetricOperation operation,
        MetricOutcome outcome)
    {
        MetricObservation observation = Assert.Single(metrics.Snapshot().Observations);
        Assert.Equal(operation, observation.Operation);
        Assert.Equal(outcome, observation.Outcome);
        Assert.Null(observation.ItemCount);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"penumbra-page-store-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string PagePath => Path.Combine(_path, "page.pen");

        public IReadOnlyList<string> TemporaryFiles() => Directory
            .GetFiles(_path, "page.pen.penumbra-*.tmp", SearchOption.TopDirectoryOnly);

        public string CreateTemporaryFile(PenumbraDocument document)
        {
            string path = Path.Combine(_path, $"page.pen.penumbra-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(path, PenumbraDocumentSerializer.Serialize(document));
            return path;
        }

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    private sealed class TestFaultInjector : IPageStoreFaultInjector
    {
        public bool ForceReplaceFallback { get; init; }

        public Func<string, long, CancellationToken, ValueTask>? AfterTemporaryFileFlushed { get; init; }

        public Func<string, string, long, CancellationToken, ValueTask>? BeforeCommit { get; init; }

        public ValueTask AfterTemporaryFileFlushedAsync(
            string temporaryPath,
            long generation,
            CancellationToken cancellationToken) =>
            AfterTemporaryFileFlushed?.Invoke(temporaryPath, generation, cancellationToken) ??
            ValueTask.CompletedTask;

        public ValueTask BeforeCommitAsync(
            string temporaryPath,
            string destinationPath,
            long generation,
            CancellationToken cancellationToken) =>
            BeforeCommit?.Invoke(temporaryPath, destinationPath, generation, cancellationToken) ??
            ValueTask.CompletedTask;
    }

    private sealed class BlockingCommitInjector : IPageStoreFaultInjector
    {
        private readonly long _generationToBlock;
        private readonly TaskCompletionSource<bool> _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingCommitInjector(long generationToBlock) => _generationToBlock = generationToBlock;

        public bool ForceReplaceFallback => false;

        public ValueTask AfterTemporaryFileFlushedAsync(
            string temporaryPath,
            long generation,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask BeforeCommitAsync(
            string temporaryPath,
            string destinationPath,
            long generation,
            CancellationToken cancellationToken)
        {
            if (generation != _generationToBlock)
            {
                return;
            }

            _blocked.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _release.TrySetResult(true);
    }

    private sealed class CancellingMetricsSink : ILocalMetricsSink
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly List<MetricObservation> _observations = new();

        public CancellingMetricsSink(CancellationTokenSource cancellation) => _cancellation = cancellation;

        public IReadOnlyList<MetricObservation> Observations => _observations;

        public void Record(MetricObservation observation)
        {
            _observations.Add(observation);
            _cancellation.Cancel();
        }
    }
}
