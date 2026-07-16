using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Penumbra.Core;

/// <summary>
/// Crash-safe filesystem page store using validated same-directory temporary files and an adjacent
/// last-known-good backup. All instances in one process share per-destination write serialization and
/// overlapping-generation state.
/// </summary>
/// <remarks>
/// Independently constructed writers whose save calls overlap must use comparable generations. Once no
/// save is active, the next request may begin a new generation epoch. This is not a multi-process lease.
/// </remarks>
public sealed class FileSystemPageStore : IPageStore
{
    internal const int MaxTemporaryFilesExaminedPerSweep = 64;
    internal const int MaxTemporaryFilesDeletedPerSweep = 16;
    internal static readonly TimeSpan StaleTemporaryFileAge = TimeSpan.FromHours(24);

    private static readonly ConcurrentDictionary<string, SharedDestinationState> SharedDestinations = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ILocalMetricsSink _metricsSink;
    private readonly TimeProvider _timeProvider;
    private readonly IPageStoreFaultInjector _faultInjector;

    /// <summary>Creates a page store with local metrics disabled.</summary>
    public FileSystemPageStore()
        : this(NoOpLocalMetricsSink.Instance, TimeProvider.System, NoOpPageStoreFaultInjector.Instance)
    {
    }

    /// <summary>Creates a page store with an optional monotonic clock for deterministic diagnostics.</summary>
    public FileSystemPageStore(ILocalMetricsSink metricsSink, TimeProvider? timeProvider = null)
        : this(metricsSink, timeProvider ?? TimeProvider.System, NoOpPageStoreFaultInjector.Instance)
    {
    }

    internal FileSystemPageStore(
        ILocalMetricsSink metricsSink,
        TimeProvider timeProvider,
        IPageStoreFaultInjector faultInjector)
    {
        ArgumentNullException.ThrowIfNull(metricsSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(faultInjector);

        _metricsSink = metricsSink;
        _timeProvider = timeProvider;
        _faultInjector = faultInjector;
    }

    /// <summary>Returns the adjacent last-known-good backup path for a page.</summary>
    public static string GetBackupPath(string path) => NormalizeDestinationPath(path) + ".lkg";

    /// <inheritdoc />
    public async Task<PageSaveResult> SaveAsync(
        PenumbraDocument document,
        string path,
        long generation,
        PageSaveKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                generation,
                "generation must not be negative");
        }

        MetricOperation operation = ToMetricOperation(kind);
        string destinationPath = NormalizeDestinationPath(path);
        SharedDestinationState sharedDestination = SharedDestinations.GetOrAdd(
            destinationPath,
            static _ => new SharedDestinationState());

        using MetricTimingScope timing = MetricTimingScope.Start(_metricsSink, operation, _timeProvider);
        bool gateHeld = false;
        bool activeGenerationObserved = false;
        string? temporaryPath = null;

        try
        {
            sharedDestination.ObserveGeneration(generation);
            activeGenerationObserved = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (!sharedDestination.IsLatest(generation))
            {
                timing.Refuse();
                return new PageSaveResult(PageSaveStatus.Superseded, generation);
            }

            await sharedDestination.Gate.WaitAsync(cancellationToken);
            gateHeld = true;

            if (!sharedDestination.IsLatest(generation))
            {
                timing.Refuse();
                return new PageSaveResult(PageSaveStatus.Superseded, generation);
            }

            string directory = Path.GetDirectoryName(destinationPath)!;
            Directory.CreateDirectory(directory);
            CleanupStaleTemporaryFiles(destinationPath);

            byte[] serializedDocument = Encoding.UTF8.GetBytes(PenumbraDocumentSerializer.Serialize(document));
            temporaryPath = CreateUniqueTemporaryPath(destinationPath);
            await WriteDurablyAsync(temporaryPath, serializedDocument, cancellationToken);
            await _faultInjector.AfterTemporaryFileFlushedAsync(
                temporaryPath,
                generation,
                cancellationToken);
            await ValidateTemporaryFileAsync(temporaryPath, serializedDocument, cancellationToken);

            ReadAttempt current = await TryReadAsync(destinationPath, cancellationToken);
            await _faultInjector.BeforeCommitAsync(
                temporaryPath,
                destinationPath,
                generation,
                cancellationToken);

            if (!CanCommit(sharedDestination, generation, cancellationToken))
            {
                timing.Refuse();
                return new PageSaveResult(PageSaveStatus.Superseded, generation);
            }

            bool committed = await CommitAsync(
                temporaryPath,
                destinationPath,
                current,
                sharedDestination,
                generation,
                cancellationToken);

            if (!committed)
            {
                timing.Refuse();
                return new PageSaveResult(PageSaveStatus.Superseded, generation);
            }

            temporaryPath = null;
            CleanupStaleTemporaryFiles(destinationPath);
            timing.Complete();
            return new PageSaveResult(PageSaveStatus.Committed, generation);
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
        finally
        {
            DeleteTemporaryFileBestEffort(temporaryPath);
            if (activeGenerationObserved)
            {
                sharedDestination.ReleaseGeneration();
            }

            if (gateHeld)
            {
                sharedDestination.Gate.Release();
            }
        }
    }

    /// <inheritdoc />
    public async Task<PageOpenResult> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string destinationPath = NormalizeDestinationPath(path);
        SharedDestinationState destination = SharedDestinations.GetOrAdd(
            destinationPath,
            static _ => new SharedDestinationState());
        using MetricTimingScope timing = MetricTimingScope.Start(
            _metricsSink,
            MetricOperation.RecoveryRead,
            _timeProvider);
        bool gateHeld = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await destination.Gate.WaitAsync(cancellationToken);
            gateHeld = true;

            ReadAttempt current = await TryReadAsync(destinationPath, cancellationToken);
            PageOpenResult result;
            if (current.Status == ReadStatus.Valid)
            {
                result = new PageOpenResult(PageOpenStatus.Current, current.Document);
            }
            else
            {
                ReadAttempt backup = await TryReadAsync(GetBackupPath(destinationPath), cancellationToken);
                result = BuildRecoveryResult(current, backup);
            }

            CleanupStaleTemporaryFiles(destinationPath);
            if (result.Status == PageOpenStatus.Unrecoverable)
            {
                timing.Refuse();
            }
            else
            {
                timing.Complete();
            }

            return result;
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
        finally
        {
            if (gateHeld)
            {
                destination.Gate.Release();
            }
        }
    }

    private static MetricOperation ToMetricOperation(PageSaveKind kind) => kind switch
    {
        PageSaveKind.Explicit => MetricOperation.ExplicitSave,
        PageSaveKind.Autosave => MetricOperation.Autosave,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "kind must be a defined save kind"),
    };

    private static string NormalizeDestinationPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
        {
            throw new ArgumentException("A page path must include a file name.", nameof(path));
        }

        return fullPath;
    }

    private static string CreateUniqueTemporaryPath(string destinationPath)
    {
        string directory = Path.GetDirectoryName(destinationPath)!;
        string fileName = Path.GetFileName(destinationPath);
        return Path.Combine(directory, $"{fileName}.penumbra-{Guid.NewGuid():N}.tmp");
    }

    private static async Task WriteDurablyAsync(
        string temporaryPath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        stream.Flush(flushToDisk: true);
    }

    private static async Task ValidateTemporaryFileAsync(
        string temporaryPath,
        byte[] expectedContents,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] actualContents = await File.ReadAllBytesAsync(temporaryPath, cancellationToken);
            if (actualContents.Length != expectedContents.Length ||
                !CryptographicOperations.FixedTimeEquals(actualContents, expectedContents))
            {
                throw new PageStoreValidationException(
                    "The flushed temporary page did not match its immutable source snapshot.");
            }

            _ = PenumbraDocumentSerializer.Deserialize(Encoding.UTF8.GetString(actualContents));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PageStoreValidationException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or FormatException or NotSupportedException)
        {
            throw new PageStoreValidationException(
                "The flushed temporary page could not be validated.",
                error);
        }
    }

    private async Task<bool> CommitAsync(
        string temporaryPath,
        string destinationPath,
        ReadAttempt current,
        SharedDestinationState sharedDestination,
        long generation,
        CancellationToken cancellationToken)
    {
        if (current.Status == ReadStatus.Missing)
        {
            if (!CanCommit(sharedDestination, generation, cancellationToken))
            {
                return false;
            }

            File.Move(temporaryPath, destinationPath);
            return true;
        }

        if (current.Status == ReadStatus.Invalid)
        {
            // A corrupt current file is not eligible to replace a known-good backup.
            if (!CanCommit(sharedDestination, generation, cancellationToken))
            {
                return false;
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return true;
        }

        string backupPath = GetBackupPath(destinationPath);
        Exception? replaceFailure = null;
        if (!_faultInjector.ForceReplaceFallback)
        {
            if (!CanCommit(sharedDestination, generation, cancellationToken))
            {
                return false;
            }

            try
            {
                File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                return true;
            }
            catch (Exception error) when (error is PlatformNotSupportedException or IOException)
            {
                replaceFailure = error;
            }
        }

        // File.Replace is not universal. Only fall back while both files still validate: this avoids
        // interpreting a partially completed or externally raced replacement as safe.
        ReadAttempt currentAfterReplace = await TryReadAsync(destinationPath, cancellationToken);
        if (currentAfterReplace.Status != ReadStatus.Valid || !File.Exists(temporaryPath))
        {
            throw new IOException(
                "Atomic page replacement failed and safe fallback preconditions were not met.",
                replaceFailure);
        }

        return await CommitWithSameDirectoryFallbackAsync(
            temporaryPath,
            destinationPath,
            backupPath,
            sharedDestination,
            generation,
            cancellationToken);
    }

    private static async Task<bool> CommitWithSameDirectoryFallbackAsync(
        string temporaryPath,
        string destinationPath,
        string backupPath,
        SharedDestinationState sharedDestination,
        long generation,
        CancellationToken cancellationToken)
    {
        string backupTemporaryPath = CreateUniqueTemporaryPath(destinationPath);
        try
        {
            await CopyDurablyAsync(destinationPath, backupTemporaryPath, cancellationToken);
            ReadAttempt copiedBackup = await TryReadAsync(backupTemporaryPath, cancellationToken);
            if (copiedBackup.Status != ReadStatus.Valid)
            {
                throw new PageStoreValidationException(
                    "The previous current page could not be validated as a fallback backup.");
            }

            if (!CanCommit(sharedDestination, generation, cancellationToken))
            {
                return false;
            }

            File.Move(backupTemporaryPath, backupPath, overwrite: true);
            backupTemporaryPath = string.Empty;

            // Publishing the backup is harmless if a newer generation arrives, but the old page must
            // not replace current after that point.
            if (!CanCommit(sharedDestination, generation, cancellationToken))
            {
                return false;
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            return true;
        }
        finally
        {
            DeleteTemporaryFileBestEffort(backupTemporaryPath);
        }
    }

    private static async Task CopyDurablyAsync(
        string sourcePath,
        string temporaryPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        destination.Flush(flushToDisk: true);
    }

    private static bool CanCommit(
        SharedDestinationState sharedDestination,
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return sharedDestination.IsLatest(generation);
    }

    private static PageOpenResult BuildRecoveryResult(ReadAttempt current, ReadAttempt backup)
    {
        if (backup.Status == ReadStatus.Valid)
        {
            return new PageOpenResult(PageOpenStatus.BackupRecoveryCandidate, backup.Document);
        }

        if (current.Status == ReadStatus.Missing && backup.Status == ReadStatus.Missing)
        {
            return new PageOpenResult(PageOpenStatus.NotFound, null);
        }

        return new PageOpenResult(PageOpenStatus.Unrecoverable, null);
    }

    private static async Task<ReadAttempt> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            PenumbraDocument document = await PenumbraDocumentSerializer.LoadAsync(path, cancellationToken);
            return new ReadAttempt(ReadStatus.Valid, document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ReadAttempt(ReadStatus.Missing, null);
        }
        catch (Exception error) when (error is JsonException or FormatException or NotSupportedException)
        {
            return new ReadAttempt(ReadStatus.Invalid, null);
        }
    }

    private void CleanupStaleTemporaryFiles(string destinationPath)
    {
        string directory = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(directory))
        {
            return;
        }

        string temporaryPrefix = $"{Path.GetFileName(destinationPath)}.penumbra-";
        StringComparison fileNameComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        DateTimeOffset cutoff = _timeProvider.GetUtcNow() - StaleTemporaryFileAge;
        int examined = 0;
        int deleted = 0;

        try
        {
            foreach (string candidate in Directory.EnumerateFiles(
                directory,
                $"{temporaryPrefix}*.tmp",
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string candidateName = Path.GetFileName(candidate);
                    if (!candidateName.StartsWith(temporaryPrefix, fileNameComparison))
                    {
                        continue;
                    }

                    if (++examined > MaxTemporaryFilesExaminedPerSweep ||
                        deleted >= MaxTemporaryFilesDeletedPerSweep)
                    {
                        break;
                    }

                    if (File.GetLastWriteTimeUtc(candidate) <= cutoff.UtcDateTime)
                    {
                        File.Delete(candidate);
                        deleted++;
                    }
                }
                catch (IOException)
                {
                    // Another process may own a fresh/in-flight temp. Cleanup is deliberately best effort.
                }
                catch (UnauthorizedAccessException)
                {
                    // Cleanup failure must not displace a valid current page.
                }
            }
        }
        catch (IOException)
        {
            // Directory enumeration is not part of the authoritative read/write decision.
        }
        catch (UnauthorizedAccessException)
        {
            // Directory enumeration is not part of the authoritative read/write decision.
        }
    }

    private static void DeleteTemporaryFileBestEffort(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A stale unique temp is safer than risking the current page during exception cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // A later bounded stale-temp sweep can retry.
        }
    }

    private enum ReadStatus
    {
        Valid,
        Missing,
        Invalid,
    }

    private sealed record ReadAttempt(ReadStatus Status, PenumbraDocument? Document);

    private sealed class SharedDestinationState
    {
        private readonly object _generationGate = new();
        private int _activeSaveCount;
        private long _latestActiveGeneration = -1;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public void ObserveGeneration(long generation)
        {
            lock (_generationGate)
            {
                if (_activeSaveCount == 0 || generation > _latestActiveGeneration)
                {
                    _latestActiveGeneration = generation;
                }

                _activeSaveCount++;
            }
        }

        public void ReleaseGeneration()
        {
            lock (_generationGate)
            {
                _activeSaveCount--;
                if (_activeSaveCount == 0)
                {
                    _latestActiveGeneration = -1;
                }
            }
        }

        public bool IsLatest(long generation)
        {
            lock (_generationGate)
            {
                return generation == _latestActiveGeneration;
            }
        }
    }
}
