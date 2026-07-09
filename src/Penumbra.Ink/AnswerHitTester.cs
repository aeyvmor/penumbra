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
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "tolerance must be non-negative");
        }

        double toleranceSq = tolerance * tolerance;
        foreach (Stroke stroke in strokes)
        {
            IReadOnlyList<StrokeSample> s = stroke.Samples;
            if (s.Count == 0)
            {
                continue;
            }

            if (s.Count == 1)
            {
                if (DistanceSq(x, y, s[0].X, s[0].Y) <= toleranceSq)
                {
                    return true;
                }

                continue;
            }

            for (int i = 1; i < s.Count; i++)
            {
                if (SegmentDistanceSq(x, y, s[i - 1].X, s[i - 1].Y, s[i].X, s[i].Y) <= toleranceSq)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double DistanceSq(double px, double py, double x, double y)
    {
        double dx = px - x, dy = py - y;
        return dx * dx + dy * dy;
    }

    // Squared distance from point P to segment AB: project P onto AB, clamp to the segment.
    private static double SegmentDistanceSq(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax, aby = by - ay;
        double lengthSq = abx * abx + aby * aby;
        if (lengthSq <= double.Epsilon)
        {
            return DistanceSq(px, py, ax, ay);
        }

        double t = Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lengthSq, 0, 1);
        return DistanceSq(px, py, ax + t * abx, ay + t * aby);
    }
}
