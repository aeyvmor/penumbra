namespace Penumbra.Core;

internal interface IPageStoreFaultInjector
{
    bool ForceReplaceFallback { get; }

    ValueTask AfterTemporaryFileFlushedAsync(
        string temporaryPath,
        long generation,
        CancellationToken cancellationToken);

    ValueTask BeforeCommitAsync(
        string temporaryPath,
        string destinationPath,
        long generation,
        CancellationToken cancellationToken);
}

internal sealed class NoOpPageStoreFaultInjector : IPageStoreFaultInjector
{
    public static NoOpPageStoreFaultInjector Instance { get; } = new();

    private NoOpPageStoreFaultInjector()
    {
    }

    public bool ForceReplaceFallback => false;

    public ValueTask AfterTemporaryFileFlushedAsync(
        string temporaryPath,
        long generation,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask BeforeCommitAsync(
        string temporaryPath,
        string destinationPath,
        long generation,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
