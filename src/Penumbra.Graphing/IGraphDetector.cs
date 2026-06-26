namespace Penumbra.Graphing;

/// <summary>Detects when recognized math should become a graph.</summary>
public interface IGraphDetector
{
    /// <summary>Returns a graph candidate when the expression is graphable.</summary>
    GraphCandidate? TryDetect(string latex);
}
