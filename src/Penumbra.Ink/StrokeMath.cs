using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>Geometry helpers shared by the ink processing primitives.</summary>
internal static class StrokeMath
{
    /// <summary>Euclidean distance between two samples in canvas space.</summary>
    public static double Distance(StrokeSample a, StrokeSample b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>Linear interpolation of position, time and pressure at <paramref name="t"/> in [0,1].</summary>
    public static StrokeSample Lerp(StrokeSample a, StrokeSample b, double t) => new(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Time + (b.Time - a.Time) * t,
        a.Pressure + (b.Pressure - a.Pressure) * t);
}
