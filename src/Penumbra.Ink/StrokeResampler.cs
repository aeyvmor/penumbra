using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Resamples a stroke to approximately uniform arc-length spacing. Raw pointer input arrives at
/// irregular spatial intervals (fast strokes leave gaps, slow ones bunch up); the recognizer and the
/// width model both want geometry that does not encode drawing speed, so we redistribute the points
/// evenly along the path. Time and pressure are interpolated along with position.
/// </summary>
public sealed class StrokeResampler
{
    private readonly double _spacing;

    /// <param name="spacing">Target distance between consecutive output samples, in canvas units.</param>
    public StrokeResampler(double spacing = 2.0)
    {
        if (spacing <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Spacing must be positive.");
        }

        _spacing = spacing;
    }

    /// <summary>Returns a copy of <paramref name="stroke"/> with samples spaced ~<c>spacing</c> apart.</summary>
    public Stroke Resample(Stroke stroke)
    {
        // A degenerate stroke has no arc length to walk; leave it untouched.
        if (stroke.Samples.Count < 2)
        {
            return stroke;
        }

        // Mutable working copy so a freshly emitted point can become the anchor for the rest of the segment.
        var points = new List<StrokeSample>(stroke.Samples);
        var output = new List<StrokeSample>(points.Count) { points[0] };

        double accumulated = 0; // distance walked since the last emitted point
        for (int i = 1; i < points.Count; i++)
        {
            StrokeSample prev = points[i - 1];
            StrokeSample curr = points[i];
            double segment = StrokeMath.Distance(prev, curr);
            if (segment <= double.Epsilon)
            {
                continue;
            }

            if (accumulated + segment >= _spacing)
            {
                double t = (_spacing - accumulated) / segment;
                StrokeSample q = StrokeMath.Lerp(prev, curr, t);
                output.Add(q);
                points.Insert(i, q); // re-walk the remainder of this segment from q
                accumulated = 0;
            }
            else
            {
                accumulated += segment;
            }
        }

        // The arc-length walk rarely lands exactly on the final point; keep the true endpoint.
        if (output[^1] != points[^1])
        {
            output.Add(points[^1]);
        }

        return stroke with { Samples = output };
    }
}
