using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Point-vs-ink hit test for the ghost-trace tap (Phase 4.5d): does a world-space point land on (or
/// within <c>tolerance</c> of) any of the answer's strokes? Distance is measured to the stroke
/// polyline's segments, not just its samples, so taps between two samples of a long straight segment
/// still count. Headless so the "did the tap hit the answer" decision is unit-tested without a canvas.
/// </summary>
public static class AnswerHitTester
{
    /// <summary>True when <paramref name="x"/>,<paramref name="y"/> is within <paramref name="tolerance"/> of any stroke.</summary>
    public static bool HitTest(IReadOnlyList<Stroke> strokes, double x, double y, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        StrokeHitGeometry.ValidateTolerance(tolerance);
        foreach (Stroke stroke in strokes)
        {
            if (StrokeHitGeometry.HitTest(stroke, x, y, tolerance))
            {
                return true;
            }
        }

        return false;
    }
}
