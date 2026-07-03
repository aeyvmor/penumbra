using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Fits captured ink into the unit em-box [0,1]×[0,1] as a pure function so banked glyphs — stored raw by
/// design (ADR-0006) — can be composed at any target size. The scale is uniform (aspect-preserving): the
/// larger of width/height spans exactly 1 and the minor axis is centered. Degenerate ink (a dot, a flat
/// bar) never divides by zero; a zero-size axis centers at 0.5. Sample Times and Pressures, stroke Ids and
/// stroke order all pass through untouched, and the input is never mutated.
/// </summary>
public static class GlyphNormalizer
{
    /// <summary>Returns <paramref name="strokes"/> uniformly scaled and translated into the unit em-box.</summary>
    public static IReadOnlyList<Stroke> ToEmBox(IReadOnlyList<Stroke> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        bool any = false;
        foreach (Stroke stroke in strokes)
        {
            foreach (StrokeSample s in stroke.Samples)
            {
                any = true;
                if (s.X < minX) minX = s.X;
                if (s.Y < minY) minY = s.Y;
                if (s.X > maxX) maxX = s.X;
                if (s.Y > maxY) maxY = s.Y;
            }
        }

        double width, height;
        if (any)
        {
            width = maxX - minX;
            height = maxY - minY;
        }
        else
        {
            // No samples at all: there is nothing to place, but preserve the empty stroke structure below.
            minX = minY = 0;
            width = height = 0;
        }

        double span = Math.Max(width, height);      // uniform scale keeps the aspect ratio
        double scale = span > 0 ? 1.0 / span : 0.0; // zero span (point / flat axis) collapses to the center

        // Center the minor axis: split the leftover after the scaled extent fills its share of the box. When
        // an axis has zero size the leftover is the whole box, so its offset is 0.5.
        double offsetX = (1.0 - width * scale) / 2.0;
        double offsetY = (1.0 - height * scale) / 2.0;

        var result = new Stroke[strokes.Count];
        for (int i = 0; i < strokes.Count; i++)
        {
            Stroke stroke = strokes[i];
            var mapped = new StrokeSample[stroke.Samples.Count];
            for (int j = 0; j < stroke.Samples.Count; j++)
            {
                StrokeSample s = stroke.Samples[j];
                mapped[j] = s with
                {
                    X = (s.X - minX) * scale + offsetX,
                    Y = (s.Y - minY) * scale + offsetY,
                };
            }

            result[i] = stroke with { Samples = mapped };
        }

        return result;
    }
}
