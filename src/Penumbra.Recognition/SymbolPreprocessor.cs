using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Turns a segmented symbol's strokes into the two inputs the R1 model expects: a
/// normalized 32x32 bitmap and the 5 geometry features. This is the C# port of the
/// Python pipeline in <c>ml/data/crohme.py</c> (render_symbol + geom_features) — it
/// must stay in step with it so app inference matches training.
///
/// Canvas/world coordinates already have Y increasing downward (like CROHME), so no
/// flip is needed. Ink is white (1.0) on black (0.0), aspect-preserved and centered.
/// </summary>
public static class SymbolPreprocessor
{
    public const int ImageSize = 32;
    public const int FeatureCount = 5;

    private const int Supersample = 4;            // render big, box-downscale -> anti-aliasing
    private const int PadFinal = 4;               // blank margin in final px
    private const double LineWidthFinal = 2.0;    // stroke width in final px

    /// <summary>Render strokes to a normalized CHW float buffer of length 1*1*32*32.</summary>
    public static float[] RenderImage(IReadOnlyList<Stroke> strokes, float mean, float std)
    {
        const int big = ImageSize * Supersample;          // 128
        const int pad = PadFinal * Supersample;           // 16
        const double radius = LineWidthFinal * Supersample / 2.0;  // 4

        if (!TryBounds(strokes, out double minX, out double minY, out double maxX, out double maxY))
        {
            return new float[ImageSize * ImageSize];   // nothing drawn -> all black
        }

        double w = maxX - minX, h = maxY - minY;
        double span = Math.Max(Math.Max(w, h), 1e-6);
        double scale = (big - 2 * pad) / span;
        double offX = pad + ((big - 2 * pad) - w * scale) / 2 - minX * scale;
        double offY = pad + ((big - 2 * pad) - h * scale) / 2 - minY * scale;

        var buf = new float[big * big];
        foreach (Stroke stroke in strokes)
        {
            IReadOnlyList<StrokeSample> s = stroke.Samples;
            if (s.Count == 0)
            {
                continue;
            }

            double PX(int i) => s[i].X * scale + offX;
            double PY(int i) => s[i].Y * scale + offY;

            if (s.Count == 1)
            {
                StampDisc(buf, big, PX(0), PY(0), radius);
                continue;
            }

            for (int i = 1; i < s.Count; i++)
            {
                StampSegment(buf, big, PX(i - 1), PY(i - 1), PX(i), PY(i), radius);
            }
        }

        // 4x4 box-average downscale to 32x32, then standardize.
        var output = new float[ImageSize * ImageSize];
        for (int oy = 0; oy < ImageSize; oy++)
        {
            for (int ox = 0; ox < ImageSize; ox++)
            {
                double sum = 0;
                for (int dy = 0; dy < Supersample; dy++)
                {
                    int by = oy * Supersample + dy;
                    for (int dx = 0; dx < Supersample; dx++)
                    {
                        sum += buf[by * big + ox * Supersample + dx];
                    }
                }

                double v = sum / (Supersample * Supersample);   // 0..1 coverage
                output[oy * ImageSize + ox] = (float)((v - mean) / std);
            }
        }

        return output;
    }

    /// <summary>
    /// The 5 geometry features (standardized), matching geom_features() in <c>crohme.py</c>.
    /// <paramref name="context"/> supplies the sibling-relative reference height and the line's
    /// vertical extent, so a symbol's size/position is judged against its neighbours — the signal
    /// segmentation unlocks (use <see cref="SymbolContext.ForSelf(IReadOnlyList{Stroke})"/> for an
    /// isolated symbol).
    /// </summary>
    public static float[] ComputeFeatures(
        IReadOnlyList<Stroke> strokes, SymbolContext context, float[] featMean, float[] featStd)
    {
        if (!TryBounds(strokes, out double minX, out double minY, out double maxX, out double maxY))
        {
            return new float[FeatureCount];
        }

        double w = maxX - minX, h = maxY - minY;
        double cy = (minY + maxY) / 2;
        double refH = context.RefHeight > 0 ? context.RefHeight : 1.0;

        double[] raw =
        {
            (w - h) / (w + h + 1e-6),                                  // aspect
            h / refH,                                                  // rel_height vs sibling symbols
            w / refH,                                                  // rel_width vs sibling symbols
            (cy - context.ExprYMin) / (context.ExprHeight + 1e-6),     // y_position in the line
            strokes.Count,                                             // stroke_count
        };

        var output = new float[FeatureCount];
        for (int i = 0; i < FeatureCount; i++)
        {
            output[i] = (float)((raw[i] - featMean[i]) / featStd[i]);
        }

        return output;
    }

    /// <summary>Combined axis-aligned bounds of the strokes (empty bounds when there are no samples).</summary>
    public static InkBounds Bounds(IReadOnlyList<Stroke> strokes)
    {
        if (!TryBounds(strokes, out double minX, out double minY, out double maxX, out double maxY))
        {
            return default;
        }

        return new InkBounds(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool TryBounds(IReadOnlyList<Stroke> strokes,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;
        bool any = false;
        foreach (Stroke stroke in strokes)
        {
            foreach (StrokeSample p in stroke.Samples)
            {
                any = true;
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        }

        return any;
    }

    private static void StampSegment(float[] buf, int size, double x0, double y0,
        double x1, double y1, double radius)
    {
        double dist = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int steps = Math.Max(1, (int)Math.Ceiling(dist));
        for (int k = 0; k <= steps; k++)
        {
            double t = (double)k / steps;
            StampDisc(buf, size, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, radius);
        }
    }

    // Soft round dot: coverage falls off over the last pixel for cheap anti-aliasing.
    private static void StampDisc(float[] buf, int size, double cx, double cy, double radius)
    {
        int x0 = Math.Max(0, (int)Math.Floor(cx - radius - 1));
        int x1 = Math.Min(size - 1, (int)Math.Ceiling(cx + radius + 1));
        int y0 = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        int y1 = Math.Min(size - 1, (int)Math.Ceiling(cy + radius + 1));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy));
                double cov = Math.Clamp(radius + 0.5 - d, 0.0, 1.0);
                int idx = y * size + x;
                if (cov > buf[idx])
                {
                    buf[idx] = (float)cov;
                }
            }
        }
    }
}
