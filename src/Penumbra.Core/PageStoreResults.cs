namespace Penumbra.Core;

/// <summary>Identifies the user-visible save path for fixed-cardinality local metrics.</summary>
public enum PageSaveKind
{
    Explicit = 0,
    Autosave = 1,
}

/// <summary>Terminal result of a page save request.</summary>
public enum PageSaveStatus
{
    Committed = 0,
    Superseded = 1,
}

/// <summary>Result of one generation-aware page save.</summary>
public readonly record struct PageSaveResult(PageSaveStatus Status, long Generation);

/// <summary>Trust decision made while opening a page and its adjacent backup.</summary>
public enum PageOpenStatus
{
    Current = 0,
    BackupRecoveryCandidate = 1,
    NotFound = 2,
    Unrecoverable = 3,
}

/// <summary>
/// Result of a page read. Backup data is returned only as a recovery candidate and is never silently
/// promoted over the current file.
/// </summary>
public sealed class PageOpenResult
{
    /// <summary>Creates a read result while enforcing the status/document invariant.</summary>
    public PageOpenResult(PageOpenStatus status, PenumbraDocument? document)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "status must be a defined open status");
        }

        bool requiresDocument = status is PageOpenStatus.Current or PageOpenStatus.BackupRecoveryCandidate;
        if (requiresDocument && document is null)
        {
            throw new ArgumentNullException(nameof(document), $"{status} requires a validated document.");
        }

        if (!requiresDocument && document is not null)
        {
            throw new ArgumentException($"{status} cannot carry a document.", nameof(document));
        }

        Status = status;
        Document = document;
    }

    /// <summary>The source/trust decision for this read.</summary>
    public PageOpenStatus Status { get; }

    /// <summary>The validated page for current/candidate results; otherwise <see langword="null"/>.</summary>
    public PenumbraDocument? Document { get; }
}

/// <summary>Signals that a flushed temporary save no longer matches its immutable source snapshot.</summary>
public sealed class PageStoreValidationException : IOException
{
    internal PageStoreValidationException(string message)
        : base(message)
    {
    }

    internal PageStoreValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
