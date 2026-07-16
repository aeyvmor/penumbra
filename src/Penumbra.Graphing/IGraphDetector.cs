using Penumbra.Core.Layout;

namespace Penumbra.Graphing;

/// <summary>
/// Detects an explicit-function graph candidate <c>y = f(x)</c> from recognized math. Two entry points read
/// the same grammar: <see cref="Detect(LayoutNode)"/> is what recognized ink supplies once Phase 5.5 grammar
/// lands; <see cref="Detect(string)"/> serves typed fixtures and the Sheet's stored LaTeX. Both must agree —
/// see the parity fixtures in <c>Penumbra.Graphing.Tests</c>.
/// </summary>
public interface IGraphDetector
{
    /// <summary>
    /// Detects a graph candidate from an accepted layout tree. Never throws for malformed/ungraphable input —
    /// see <see cref="GraphDetectionOutcome"/>.
    /// </summary>
    GraphDetectionOutcome Detect(LayoutNode root);

    /// <summary>
    /// Detects a graph candidate from a LaTeX string in the <c>Penumbra.Cas.Latex.LatexToAngouriMath</c>
    /// dialect. Never throws for malformed/ungraphable input — see <see cref="GraphDetectionOutcome"/>.
    /// </summary>
    GraphDetectionOutcome Detect(string latex);
}
