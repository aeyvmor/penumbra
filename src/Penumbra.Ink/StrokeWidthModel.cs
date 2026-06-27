using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Maps stroke samples to a rendered line width. Real pens report pressure, which drives width directly;
/// a mouse reports a constant pressure, so we fall back to velocity (faster pen = thinner ink, the way a
/// real nib behaves). Pure and headless so the look of the ink can be unit-tested without a canvas.
/// </summary>
public sealed class StrokeWidthModel
{
    private readonly double _minWidth;
    private readonly double _maxWidth;
    private readonly double _velocityHalfWidth;

    /// <param name="minWidth">Thinnest rendered width (max pressure or fastest movement).</param>
    /// <param name="maxWidth">Thickest rendered width (min pressure or a resting pen).</param>
    /// <param name="velocityHalfWidth">Speed (canvas units per ms) at which the velocity falloff is half.</param>
    public StrokeWidthModel(double minWidth = 1.0, double maxWidth = 4.0, double velocityHalfWidth = 1.5)
    {
        if (minWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minWidth), minWidth, "Width must be positive.");
        }

        if (maxWidth < minWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWidth), maxWidth, "maxWidth must be >= minWidth.");
        }

        if (velocityHalfWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(velocityHalfWidth), velocityHalfWidth, "Must be positive.");
        }

        _minWidth = minWidth;
        _maxWidth = maxWidth;
        _velocityHalfWidth = velocityHalfWidth;
    }

    /// <summary>Width from pressure: 0 → thickest, 1 → thinnest, clamped to [min,max].</summary>
    public double FromPressure(double pressure)
    {
        double p = Math.Clamp(pressure, 0, 1);
        return _maxWidth - (_maxWidth - _minWidth) * p;
    }

    /// <summary>Width from speed (canvas units per ms): a resting pen is thickest, fast movement thins it.</summary>
    public double FromVelocity(double velocity)
    {
        double v = Math.Max(0, velocity);
        double fast = v / (v + _velocityHalfWidth); // 0 at rest → approaches 1 when fast
        return _maxWidth - (_maxWidth - _minWidth) * fast;
    }

    /// <summary>
    /// Per-sample widths for a whole stroke. With <paramref name="usePressure"/> the device pressure drives
    /// width; otherwise width comes from the local pen speed derived from sample positions and timestamps.
    /// </summary>
    public IReadOnlyList<double> ComputeWidths(Stroke stroke, bool usePressure)
    {
        IReadOnlyList<StrokeSample> samples = stroke.Samples;
        var widths = new double[samples.Count];
        if (samples.Count == 0)
        {
            return widths;
        }

        if (usePressure)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                widths[i] = FromPressure(samples[i].Pressure);
            }

            return widths;
        }

        for (int i = 0; i < samples.Count; i++)
        {
            // Speed across the segment ending at i (or the first segment for the leading sample).
            StrokeSample a = samples[i == 0 ? 0 : i - 1];
            StrokeSample b = samples[i == 0 ? Math.Min(1, samples.Count - 1) : i];
            widths[i] = FromVelocity(Speed(a, b));
        }

        return widths;
    }

    private static double Speed(StrokeSample a, StrokeSample b)
    {
        double dt = (b.Time - a.Time).TotalMilliseconds;
        if (dt <= double.Epsilon)
        {
            return 0; // simultaneous or out-of-order samples read as "at rest"
        }

        return StrokeMath.Distance(a, b) / dt;
    }
}
