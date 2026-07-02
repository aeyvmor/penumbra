using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Turns a confident recognition into glyph exemplars for the bank (Phase 3.9d). Only tokens at or above
/// the confidence threshold are kept — a shaky read must not poison the corpus with mislabelled ink — and
/// each surviving token is materialized from the source strokes it aligns to via Seam 1. Headless so the
/// confidence filter and stroke resolution are unit-tested without the UI.
/// </summary>
public static class GlyphCapture
{
    /// <summary>
    /// Collects one <see cref="GlyphSample"/> per token whose confidence meets <paramref name="threshold"/>,
    /// resolving each token's source strokes from <paramref name="strokes"/> by id. Tokens whose strokes
    /// can't be found are skipped.
    /// </summary>
    public static IReadOnlyList<GlyphSample> Collect(
        IReadOnlyList<RecognizedToken> tokens,
        IReadOnlyList<Stroke> strokes,
        double threshold,
        DateTimeOffset capturedAt,
        string deviceClass = "unknown")
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(strokes);

        Dictionary<Guid, Stroke> byId = strokes.ToDictionary(s => s.Id);
        var samples = new List<GlyphSample>();

        foreach (RecognizedToken token in tokens)
        {
            if (token.Confidence < threshold)
            {
                continue;
            }

            var glyphStrokes = new List<Stroke>(token.SourceStrokeIds.Count);
            foreach (Guid id in token.SourceStrokeIds)
            {
                if (byId.TryGetValue(id, out Stroke? stroke))
                {
                    glyphStrokes.Add(stroke);
                }
            }

            if (glyphStrokes.Count > 0)
            {
                samples.Add(new GlyphSample(token.Latex, glyphStrokes, capturedAt, deviceClass));
            }
        }

        return samples;
    }
}
