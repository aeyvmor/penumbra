using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Turns an answer string into laid-out, jittered, timed strokes plus a master timeline — the synthesis
/// half of Seam 4 (Phase 4c). It tokenizes the text glyph-by-glyph, pulls each glyph's ink from an ordered
/// priority chain of <see cref="IGlyphSource"/> (first non-null wins), applies small seeded affine jitter
/// so repeated digits differ, places each glyph on the baseline to the right of the anchor, re-times any
/// glyph that carries no captured pace, and emits a <see cref="StrokeTimeline"/> with pen-up air moves.
/// M2 ships affine-only jitter; elastic warp rides on this same seam later.
/// </summary>
public sealed class HandwritingSynthesizer
{
    private readonly IReadOnlyList<IGlyphSource> _sources;

    /// <summary>Builds a synthesizer over an ordered source chain; earlier sources take priority.</summary>
    public HandwritingSynthesizer(IReadOnlyList<IGlyphSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources;
    }

    /// <summary>
    /// Lays out <paramref name="text"/> as handwriting anchored to the right of <paramref name="anchor"/>.
    /// Returns null only when the text carries no glyph tokens at all (empty or all-whitespace); when glyphs
    /// are present but every source declines them, returns a non-null result with empty strokes so the
    /// caller still learns which symbols to typeset via <see cref="SynthesizedHandwriting.MissingSymbols"/>.
    /// </summary>
    public SynthesizedHandwriting? Synthesize(string text, InkBounds anchor, SynthesisOptions options, Random random)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(random);

        double lineHeight = options.LineHeight;

        // Baseline anchor: the first glyph's left ink edge sits a gap past the anchor's right edge, and every
        // glyph's em-box vertical centre lines up with the anchor's vertical centre.
        double penX = anchor.X + anchor.Width + options.GapAfterAnchor * lineHeight;
        double centerY = anchor.Y + anchor.Height / 2.0;

        var strokes = new List<Stroke>();
        var missing = new List<string>();
        bool sawGlyphToken = false;

        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                // A space is pure advance — no ink, no glyph token, no random consumed.
                penX += options.SpaceAdvance * lineHeight;
                continue;
            }

            sawGlyphToken = true;
            string symbol = options.SymbolMap.TryGetValue(ch, out string? mapped) ? mapped : ch.ToString();

            IReadOnlyList<Stroke>? emBox = Lookup(symbol, random);
            if (emBox is null)
            {
                // Total miss across the chain: the app will typeset this one. No ink, no advance.
                missing.Add(symbol);
                continue;
            }

            // Jitter in em-box space (translation is a line-height fraction, i.e. em units), then place.
            IReadOnlyList<Stroke> jittered = ApplyJitter(emBox, options, random);
            GlyphExtent extent = MeasureX(jittered);

            // Left ink edge lands exactly on penX; em-box vertical centre (0.5) lands on centerY.
            double offsetX = penX - extent.MinX * lineHeight;
            foreach (Stroke stroke in jittered)
            {
                strokes.Add(ToWorld(stroke, lineHeight, offsetX, centerY));
            }

            // Advance by this glyph's OWN scaled ink width (aspect-preserving em-box → a '1' is narrow).
            penX += (extent.MaxX - extent.MinX) * lineHeight + options.LetterSpacing * lineHeight;
        }

        if (!sawGlyphToken)
        {
            return null;
        }

        // Re-time any glyph that arrived without a captured pace (all sample times equal), then schedule
        // pen-up air moves from the travel distance between one stroke's end and the next stroke's start.
        for (int i = 0; i < strokes.Count; i++)
        {
            strokes[i] = RetimeIfPaceless(strokes[i], options.PenSpeed);
        }

        var airMoves = BuildAirMoves(strokes, options);
        var timeline = new StrokeTimeline(strokes, airMoves);
        return new SynthesizedHandwriting(strokes, timeline, missing);
    }

    /// <summary>Walks the chain, returning the first source's ink for the symbol; null only on total miss.</summary>
    private IReadOnlyList<Stroke>? Lookup(string symbol, Random random)
    {
        foreach (IGlyphSource source in _sources)
        {
            IReadOnlyList<Stroke>? glyph = source.GetGlyph(symbol, random);
            if (glyph is not null)
            {
                return glyph;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies a per-glyph affine jitter — uniform scale, rotation, and translation — about the em-box
    /// centre (0.5,0.5). Draw order (scale, rotation, tx, ty) is fixed so a seed reproduces geometry
    /// byte-for-byte. Copies every sample; the source strokes are never mutated or aliased.
    /// </summary>
    private static IReadOnlyList<Stroke> ApplyJitter(IReadOnlyList<Stroke> emBox, SynthesisOptions options, Random random)
    {
        double scale = 1.0 + Signed(random, options.ScaleJitter);
        double radians = Signed(random, options.RotationJitterDegrees) * Math.PI / 180.0;
        double tx = Signed(random, options.TranslationJitter);
        double ty = Signed(random, options.TranslationJitter);
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        var result = new Stroke[emBox.Count];
        for (int s = 0; s < emBox.Count; s++)
        {
            Stroke stroke = emBox[s];
            var mapped = new StrokeSample[stroke.Samples.Count];
            for (int j = 0; j < stroke.Samples.Count; j++)
            {
                StrokeSample p = stroke.Samples[j];
                double dx = (p.X - 0.5) * scale;
                double dy = (p.Y - 0.5) * scale;
                mapped[j] = p with
                {
                    X = 0.5 + dx * cos - dy * sin + tx,
                    Y = 0.5 + dx * sin + dy * cos + ty,
                };
            }

            // Fresh Id: this is new synthesized ink, not the banked exemplar.
            result[s] = new Stroke(Guid.NewGuid(), mapped);
        }

        return result;
    }

    /// <summary>Maps a jittered em-box stroke into world space: scale by line height, then translate.</summary>
    private static Stroke ToWorld(Stroke stroke, double lineHeight, double offsetX, double centerY)
    {
        var mapped = new StrokeSample[stroke.Samples.Count];
        for (int j = 0; j < stroke.Samples.Count; j++)
        {
            StrokeSample p = stroke.Samples[j];
            mapped[j] = p with
            {
                X = p.X * lineHeight + offsetX,
                Y = (p.Y - 0.5) * lineHeight + centerY,
            };
        }

        return stroke with { Samples = mapped };
    }

    /// <summary>
    /// Gives synthetic times to a stroke whose samples all share one timestamp (banked/stub ink carries no
    /// captured pace): time at each sample = cumulative arc length so far ÷ pen speed. Strokes that already
    /// carry a real captured pace keep it untouched.
    /// </summary>
    private static Stroke RetimeIfPaceless(Stroke stroke, double penSpeed)
    {
        IReadOnlyList<StrokeSample> samples = stroke.Samples;
        if (samples.Count < 2)
        {
            return stroke;
        }

        TimeSpan min = samples[0].Time, max = samples[0].Time;
        foreach (StrokeSample s in samples)
        {
            if (s.Time < min) min = s.Time;
            if (s.Time > max) max = s.Time;
        }

        if (max > min)
        {
            return stroke; // real captured pace — leave it alone.
        }

        var retimed = new StrokeSample[samples.Count];
        retimed[0] = samples[0] with { Time = TimeSpan.Zero };
        double arc = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            arc += StrokeMath.Distance(samples[i - 1], samples[i]);
            retimed[i] = samples[i] with { Time = TimeSpan.FromSeconds(arc / penSpeed) };
        }

        return stroke with { Samples = retimed };
    }

    /// <summary>Pen-up gap before each stroke after the first, from travel distance clamped to the options range.</summary>
    private static IReadOnlyList<TimeSpan> BuildAirMoves(IReadOnlyList<Stroke> strokes, SynthesisOptions options)
    {
        int gaps = Math.Max(0, strokes.Count - 1);
        var airMoves = new TimeSpan[gaps];
        for (int i = 1; i < strokes.Count; i++)
        {
            IReadOnlyList<StrokeSample> prev = strokes[i - 1].Samples;
            IReadOnlyList<StrokeSample> next = strokes[i].Samples;
            TimeSpan gap = options.MinAirMove;
            if (prev.Count > 0 && next.Count > 0)
            {
                double dist = StrokeMath.Distance(prev[^1], next[0]);
                gap = TimeSpan.FromSeconds(dist / options.PenSpeed);
            }

            if (gap < options.MinAirMove) gap = options.MinAirMove;
            else if (gap > options.MaxAirMove) gap = options.MaxAirMove;
            airMoves[i - 1] = gap;
        }

        return airMoves;
    }

    /// <summary>Uniform draw in [-magnitude, +magnitude].</summary>
    private static double Signed(Random random, double magnitude) => (random.NextDouble() * 2.0 - 1.0) * magnitude;

    private static GlyphExtent MeasureX(IReadOnlyList<Stroke> strokes)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        foreach (Stroke stroke in strokes)
        {
            foreach (StrokeSample s in stroke.Samples)
            {
                if (s.X < minX) minX = s.X;
                if (s.X > maxX) maxX = s.X;
            }
        }

        // A glyph with no samples has no extent; collapse to a zero-width slot at the origin.
        if (double.IsPositiveInfinity(minX))
        {
            return new GlyphExtent(0, 0);
        }

        return new GlyphExtent(minX, maxX);
    }

    private readonly record struct GlyphExtent(double MinX, double MaxX);
}
