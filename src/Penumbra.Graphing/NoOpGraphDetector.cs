using Penumbra.Core.Layout;

namespace Penumbra.Graphing;

/// <summary>
/// Placeholder <see cref="IGraphDetector"/> that never accepts a candidate. Still referenced by the App's DI
/// registration until the ScottPlot panel task switches it to <see cref="GraphDetector"/> — kept, rather than
/// removed, so that wiring stays a one-line swap.
/// </summary>
public sealed class NoOpGraphDetector : IGraphDetector
{
    /// <inheritdoc />
    public GraphDetectionOutcome Detect(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return GraphDetectionOutcome.Rejected(
            GraphRejectionReason.UnsupportedConstruct, "graph detection is not yet enabled");
    }

    /// <inheritdoc />
    public GraphDetectionOutcome Detect(string latex)
    {
        ArgumentNullException.ThrowIfNull(latex);

        return GraphDetectionOutcome.Rejected(
            GraphRejectionReason.UnsupportedConstruct, "graph detection is not yet enabled");
    }
}
