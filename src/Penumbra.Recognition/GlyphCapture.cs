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
    /// Fallback banking bar — higher than the compute/reject bar
    /// (<see cref="RecognitionCalibration.DefaultMinConfidence"/> = 0.55) on purpose. A wrong computed
    /// answer is visible and self-correcting; a poisoned exemplar is silent and corrupts every future
    /// synthesized answer for that symbol. So we only bank ink we are quite sure of. A calibrated model
    /// ships a re-fitted bar as <c>bank_confidence</c> in meta.json
    /// (<see cref="RecognitionCalibration.BankConfidence"/>); this constant is the default when it doesn't.
    /// </summary>
    public const double BankThreshold = 0.80;

    /// <summary>
    /// Collects one <see cref="GlyphSample"/> per token whose confidence meets <paramref name="threshold"/>
    /// and whose symbol is bankable per <see cref="GlyphBankPolicy"/> (letters, brackets, stray misreads are
    /// skipped so junk never enters the corpus even at high confidence), resolving each token's source strokes
    /// from <paramref name="strokes"/> by id. Tokens whose strokes can't be found are skipped.
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
            // B4 defense-in-depth: an energy-rejected token is OOD no matter how confident its softmax —
            // the gate already refuses such lines before banking, but a poisoned exemplar is the silent
            // failure this class exists to prevent, so the rejection is enforced here too.
            if (token.Confidence < threshold || token.Rejected)
            {
                continue;
            }

            if (!GlyphBankPolicy.IsBankable(token.Latex))
            {
                continue;   // not a symbol synthesis can emit — never worth banking
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

    /// <summary>
    /// Live-mode variant (Phase 4.5b): recognition now re-runs on every pen-lift, so the same physical
    /// ink is re-read many times — without a memory, every re-read would deposit a duplicate exemplar
    /// and skew the bank's recency sampling. This overload skips any sample whose source-stroke set is
    /// already in <paramref name="bankedStrokeSets"/> and records the sets it emits, so one physical
    /// glyph banks exactly once no matter how many reads see it.
    /// </summary>
    public static IReadOnlyList<GlyphSample> Collect(
        IReadOnlyList<RecognizedToken> tokens,
        IReadOnlyList<Stroke> strokes,
        double threshold,
        DateTimeOffset capturedAt,
        ISet<string> bankedStrokeSets,
        string deviceClass = "unknown")
    {
        ArgumentNullException.ThrowIfNull(bankedStrokeSets);

        var fresh = new List<GlyphSample>();
        foreach (GlyphSample sample in Collect(tokens, strokes, threshold, capturedAt, deviceClass))
        {
            if (bankedStrokeSets.Add(StrokeSetKey(sample.Strokes.Select(s => s.Id))))
            {
                fresh.Add(sample);
            }
        }

        return fresh;
    }

    /// <summary>
    /// Canonical identity of a set of strokes, independent of order — the unit of "this physical ink
    /// was already banked".
    /// </summary>
    public static string StrokeSetKey(IEnumerable<Guid> strokeIds)
    {
        ArgumentNullException.ThrowIfNull(strokeIds);
        return string.Join('+', strokeIds.Select(id => id.ToString("N")).OrderBy(s => s, StringComparer.Ordinal));
    }
}
