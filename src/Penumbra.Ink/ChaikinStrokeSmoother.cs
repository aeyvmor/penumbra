using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Smooths a stroke with Chaikin corner-cutting. Each pass replaces every interior segment with two
/// points at 1/4 and 3/4, rounding off sharp corners while staying close to the original path. The
/// two endpoints are preserved so the ink still starts and ends where the pen touched down and lifted.
/// </summary>
public sealed class ChaikinStrokeSmoother : IStrokeSmoother
{
    private readonly int _iterations;

    /// <param name="iterations">Number of corner-cutting passes; more passes = smoother, rounder ink.</param>
    public ChaikinStrokeSmoother(int iterations = 2)
    {
        if (iterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), iterations, "Iterations cannot be negative.");
        }

        _iterations = iterations;
    }

    /// <inheritdoc />
    public Stroke Smooth(Stroke stroke)
    {
        // Nothing to round off below three points, and zero iterations is an explicit no-op.
        if (_iterations == 0 || stroke.Samples.Count < 3)
        {
            return stroke;
        }

        IReadOnlyList<StrokeSample> points = stroke.Samples;
        for (int pass = 0; pass < _iterations; pass++)
        {
            points = CutCorners(points);
        }

        return stroke with { Samples = points };
    }

    private static IReadOnlyList<StrokeSample> CutCorners(IReadOnlyList<StrokeSample> points)
    {
        var result = new List<StrokeSample>(points.Count * 2) { points[0] };
        for (int i = 0; i < points.Count - 1; i++)
        {
            StrokeSample p = points[i];
            StrokeSample q = points[i + 1];
            result.Add(StrokeMath.Lerp(p, q, 0.25));
            result.Add(StrokeMath.Lerp(p, q, 0.75));
        }

        result.Add(points[^1]);
        return result;
    }
}
