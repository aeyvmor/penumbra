namespace Penumbra.Core;

/// <summary>Persists and recovers one Penumbra page without depending on UI state.</summary>
public interface IPageStore
{
    /// <summary>
    /// Durably saves an immutable page snapshot if <paramref name="generation"/> is still the newest
    /// overlapping generation requested for this destination. A quiescent writer may begin a new generation
    /// epoch; equal generations within one epoch must identify equivalent snapshots.
    /// </summary>
    Task<PageSaveResult> SaveAsync(
        PenumbraDocument document,
        string path,
        long generation,
        PageSaveKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the authoritative page or exposes a valid backup as an explicit recovery candidate.
    /// </summary>
    Task<PageOpenResult> OpenAsync(string path, CancellationToken cancellationToken = default);
}
