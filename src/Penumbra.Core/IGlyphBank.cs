namespace Penumbra.Core;

/// <summary>
/// A growing, per-user store of handwritten glyph exemplars (Phase 3.9d). The bank fills passively as
/// the user writes and their symbols are confidently recognized; it never samples or synthesizes ink —
/// that is Phase 4b. Implementations persist across sessions.
/// </summary>
public interface IGlyphBank
{
    /// <summary>Adds an exemplar to the bank and persists it.</summary>
    void Capture(GlyphSample sample);

    /// <summary>Whether at least one exemplar has been banked for <paramref name="symbol"/>.</summary>
    bool Has(string symbol);

    /// <summary>Every banked exemplar for <paramref name="symbol"/>, oldest first (empty if none).</summary>
    IReadOnlyList<GlyphSample> Samples(string symbol);

    /// <summary>
    /// Draws one exemplar for <paramref name="symbol"/> weighted toward recently captured ink, or null if
    /// the bank holds none. The caller supplies <paramref name="random"/> so selection is seed-deterministic
    /// under test — implementations must never fall back to a shared or wall-clock-seeded source.
    /// </summary>
    GlyphSample? Sample(string symbol, Random random);
}
