using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Headless point-to-stroke hit testing for document ink. Coordinates and <c>tolerance</c>
/// are in the caller's world space; UI code is responsible for converting a screen-space eraser radius
/// by its current zoom before calling this API.
/// </summary>
public static class StrokeHitTester
{
    /// <summary>
    /// Returns the IDs of all strokes within <paramref name="tolerance"/> of the supplied point, in
    /// document order. Duplicate IDs are returned once, at the first matching occurrence, because an
    /// erase operation addresses strokes by ID and must remain deterministic for malformed old files.
    /// </summary>
    public static IReadOnlyList<Guid> HitTest(
        IReadOnlyList<Stroke> strokes,
        double x,
        double y,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        if (double.IsNaN(tolerance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                tolerance,
                "tolerance must not be NaN");
        }

        StrokeHitGeometry.ValidateTolerance(tolerance);

        var hitIds = new List<Guid>();
        var seenHitIds = new HashSet<Guid>();
        foreach (Stroke stroke in strokes)
        {
            if (StrokeHitGeometry.HitTest(stroke, x, y, tolerance) && seenHitIds.Add(stroke.Id))
            {
                hitIds.Add(stroke.Id);
            }
        }

        return hitIds;
    }
}

/// <summary>
/// Shared geometry kernel for document erasing and synthesized-answer tapping. Keeping projection
/// math in one place prevents the two interactions from acquiring subtly different dot/segment rules.
/// </summary>
internal static class StrokeHitGeometry
{
    public static void ValidateTolerance(double tolerance)
    {
        // Deliberately matches AnswerHitTester's original validation semantics. StrokeHitTester adds
        // its stronger NaN guard at its public boundary without changing the established answer API.
        if (tolerance < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tolerance),
                tolerance,
                "tolerance must be non-negative");
        }
    }

    public static bool HitTest(Stroke stroke, double x, double y, double tolerance)
    {
        IReadOnlyList<StrokeSample> samples = stroke.Samples;
        if (samples.Count == 0)
        {
            return false;
        }

        double toleranceSq = tolerance * tolerance;
        if (samples.Count == 1)
        {
            return DistanceSq(x, y, samples[0].X, samples[0].Y) <= toleranceSq;
        }

        for (int i = 1; i < samples.Count; i++)
        {
            if (SegmentDistanceSq(
                x,
                y,
                samples[i - 1].X,
                samples[i - 1].Y,
                samples[i].X,
                samples[i].Y) <= toleranceSq)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceSq(double px, double py, double x, double y)
    {
        double dx = px - x;
        double dy = py - y;
        return dx * dx + dy * dy;
    }

    // Squared distance from point P to segment AB: project P onto AB, then clamp to the segment.
    private static double SegmentDistanceSq(double px, double py, double ax, double ay, double bx, double by)
    {
        double abx = bx - ax;
        double aby = by - ay;
        double lengthSq = abx * abx + aby * aby;
        if (lengthSq <= double.Epsilon)
        {
            return DistanceSq(px, py, ax, ay);
        }

        double t = Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lengthSq, 0, 1);
        return DistanceSq(px, py, ax + t * abx, ay + t * aby);
    }
}
