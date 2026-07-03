using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Supplies one glyph's ink for a requested output symbol, as EM-BOX-NORMALIZED strokes (unit box
/// [0,1]×[0,1], aspect-preserving — see <see cref="GlyphNormalizer"/>). Returns null when this source
/// cannot supply the symbol, so a <see cref="HandwritingSynthesizer"/> can fall through a priority chain
/// (user's own bank first, typeset-font cold-start fallback next — the Phase 4e seam plugs in here).
/// </summary>
public interface IGlyphSource
{
    /// <summary>
    /// Em-box-normalized strokes for <paramref name="symbol"/>, or null if this source lacks it.
    /// <paramref name="random"/> is selection entropy (a bank draws a weighted exemplar); seed it for tests.
    /// </summary>
    IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random);
}
