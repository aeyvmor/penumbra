using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Produces geometrically transformed COPIES of strokes — the input is never mutated. Every returned
/// stroke carries a FRESH id (mandatory): stroke ids are identity keys in the .pen v3 load cache and
/// in Seam-1 stroke↔token alignment, so a transformed copy reusing its source id would collide with
/// the original and corrupt id-keyed state.
/// </summary>
public static class StrokeTransformer
{
    /// <summary>
    /// Returns copies of <paramref name="strokes"/> scaled uniformly by <paramref name="scale"/>
    /// about the point (<paramref name="originX"/>, <paramref name="originY"/>), THEN translated by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) — i.e. each sample maps to
    /// <c>origin + (p − origin)·scale + (dx, dy)</c>. Sample <c>Time</c> and <c>Pressure</c> are
    /// preserved exactly (the render timeline replays them unchanged); sample order and stroke order
    /// are preserved; empty input yields empty output. Output ids are fresh and unique.
    /// </summary>
    public static IReadOnlyList<Stroke> Transform(
        IReadOnlyList<Stroke> strokes,
        double dx,
        double dy,
        double scale = 1.0,
        double originX = 0,
        double originY = 0)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        var transformed = new List<Stroke>(strokes.Count);
        foreach (Stroke stroke in strokes)
        {
            var samples = new List<StrokeSample>(stroke.Samples.Count);
            foreach (StrokeSample sample in stroke.Samples)
            {
                samples.Add(sample with
                {
                    X = originX + (sample.X - originX) * scale + dx,
                    Y = originY + (sample.Y - originY) * scale + dy,
                });
            }

            transformed.Add(new Stroke(Guid.NewGuid(), samples));
        }

        return transformed;
    }
}
