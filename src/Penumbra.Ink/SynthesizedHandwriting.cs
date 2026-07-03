using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// The result of laying out an answer string as handwriting: the world-coordinate strokes (fresh Ids —
/// new ink, never the banked originals), the master <see cref="StrokeTimeline"/> built from exactly those
/// strokes, and the symbols no source could supply (the app typesets those as a fallback).
/// </summary>
/// <param name="Strokes">Laid-out, jittered, re-timed strokes in world coordinates, in draw order.</param>
/// <param name="Timeline">The Seam-4 timeline over <paramref name="Strokes"/> with inter-stroke air moves.</param>
/// <param name="MissingSymbols">Glyph labels that every source in the chain declined, in text order.</param>
public sealed record SynthesizedHandwriting(
    IReadOnlyList<Stroke> Strokes,
    StrokeTimeline Timeline,
    IReadOnlyList<string> MissingSymbols);
