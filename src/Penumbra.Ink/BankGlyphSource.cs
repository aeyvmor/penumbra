using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// An <see cref="IGlyphSource"/> over the user's own <see cref="IGlyphBank"/>: draws a recency-weighted
/// exemplar and normalizes it to the em-box for composition. The bank stores strokes RAW (ADR-0006), so
/// normalization happens here, not at capture. Null when the bank holds no exemplar for the symbol.
/// </summary>
public sealed class BankGlyphSource : IGlyphSource
{
    private readonly IGlyphBank _bank;

    /// <summary>Wraps <paramref name="bank"/> as a glyph source.</summary>
    public BankGlyphSource(IGlyphBank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        _bank = bank;
    }

    /// <inheritdoc />
    public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(random);

        GlyphSample? sample = _bank.Sample(symbol, random);
        return sample is null ? null : GlyphNormalizer.ToEmBox(sample.Strokes);
    }
}
