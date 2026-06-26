namespace Penumbra.Graphing;

/// <summary>Phase 0 graph detector placeholder used until ScottPlot integration lands.</summary>
public sealed class NoOpGraphDetector : IGraphDetector
{
    /// <inheritdoc />
    public GraphCandidate? TryDetect(string latex)
    {
        ArgumentNullException.ThrowIfNull(latex);

        return null;
    }
}
