using Penumbra.Core;
using SkiaSharp;

namespace Penumbra.Ink;

/// <summary>
/// Cold-start <see cref="IGlyphSource"/> that traces a handwriting font's glyph outlines into strokes when
/// the user's own bank lacks a symbol — so M2 animates from day one with zero captured glyphs. The typeface
/// is parsed once and extracted glyphs are cached (the font never changes at runtime), so the source is
/// deterministic and <c>random</c> is unused (that is part of the <see cref="IGlyphSource"/> seam contract).
/// Known M2 limitation: this traces the glyph's CONTOUR (its filled outline), not a centerline skeleton —
/// good enough for cold-start; centerline extraction is future polish.
/// </summary>
public sealed class CaveatGlyphSource : IGlyphSource
{
    // A fixed reference em size to extract outlines at; the exact value is irrelevant because every glyph is
    // re-normalized to the unit em-box afterward, but a larger size gives the path measurer more resolution.
    private const float ReferenceSize = 100f;

    // Contour sampling step (~2% of the reference size): dense enough to trace curves, sparse enough to stay cheap.
    private const float SampleStep = ReferenceSize * 0.02f;

    // Constant mid pressure — font ink carries no captured pen force.
    private const float GlyphPressure = 0.6f;

    // LaTeX-label → char map for the few multi-char symbols the answer text can carry. Single-char symbols
    // ("2", "-") pass straight through; anything else multi-char is an honest miss (null).
    private static readonly IReadOnlyDictionary<string, char> LabelToChar = new Dictionary<string, char>
    {
        ["\\times"] = '×',
        ["\\div"] = '÷',
        ["\\pi"] = 'π',
    };

    private readonly string _fontPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<Stroke>?> _cache = new(StringComparer.Ordinal);
    private SKTypeface? _typeface; // lazily parsed on first glyph request

    /// <summary>
    /// Wraps the handwriting font at <paramref name="fontPath"/>. The path's existence is validated eagerly
    /// (throws <see cref="FileNotFoundException"/>) so wiring can skip this source when the asset is absent;
    /// the typeface itself is parsed lazily on the first glyph request (throws on a corrupt font).
    /// </summary>
    public CaveatGlyphSource(string fontPath)
    {
        ArgumentNullException.ThrowIfNull(fontPath);
        if (!File.Exists(fontPath))
        {
            throw new FileNotFoundException("Caveat handwriting font not found; cold-start glyph source unavailable.", fontPath);
        }

        _fontPath = fontPath;
    }

    /// <inheritdoc />
    public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(random);

        lock (_gate)
        {
            if (_cache.TryGetValue(symbol, out IReadOnlyList<Stroke>? cached))
            {
                return cached; // hits AND honest misses are cached — the font is immutable at runtime.
            }

            IReadOnlyList<Stroke>? extracted = Extract(symbol);
            _cache[symbol] = extracted;
            return extracted;
        }
    }

    /// <summary>Traces the glyph outline for <paramref name="symbol"/> into em-box strokes, or null on any miss.</summary>
    private IReadOnlyList<Stroke>? Extract(string symbol)
    {
        if (!TryMapChar(symbol, out char ch))
        {
            return null; // unknown multi-char symbol — nothing to trace.
        }

        SKTypeface typeface = Typeface();
        using var font = new SKFont(typeface, ReferenceSize);

        // Honest .notdef check: glyph id 0 means the font has no outline for this char — never render tofu.
        ushort glyphId = font.GetGlyph(ch);
        if (glyphId == 0)
        {
            return null;
        }

        using SKPath? path = font.GetGlyphPath(glyphId);
        if (path is null || path.IsEmpty)
        {
            return null;
        }

        var strokes = new List<Stroke>();
        using (var measure = new SKPathMeasure(path, forceClosed: false))
        {
            do
            {
                float length = measure.Length;
                if (length <= 0f)
                {
                    continue; // degenerate contour (e.g. a stray move) — no ink.
                }

                var samples = new List<StrokeSample>();
                for (float d = 0f; d < length; d += SampleStep)
                {
                    if (measure.GetPosition(d, out SKPoint p))
                    {
                        samples.Add(new StrokeSample(p.X, p.Y, TimeSpan.Zero, GlyphPressure));
                    }
                }

                // Always close the contour on its final point so the traced outline meets itself.
                if (measure.GetPosition(length, out SKPoint end))
                {
                    samples.Add(new StrokeSample(end.X, end.Y, TimeSpan.Zero, GlyphPressure));
                }

                if (samples.Count > 0)
                {
                    strokes.Add(new Stroke(Guid.NewGuid(), samples));
                }
            }
            while (measure.NextContour());
        }

        if (strokes.Count == 0)
        {
            return null;
        }

        // Per-glyph em-box normalization matches the bank source's contract, so sizing stays consistent when
        // an answer mixes bank glyphs and font glyphs.
        return GlyphNormalizer.ToEmBox(strokes);
    }

    /// <summary>Maps an output symbol to the char to trace: single-char pass-through, then the LaTeX-label map.</summary>
    private static bool TryMapChar(string symbol, out char ch)
    {
        if (symbol.Length == 1)
        {
            ch = symbol[0];
            return true;
        }

        return LabelToChar.TryGetValue(symbol, out ch);
    }

    /// <summary>Lazily parses the typeface once; throws a clear error if the font file is corrupt/unreadable.</summary>
    private SKTypeface Typeface()
    {
        // Called under _gate.
        if (_typeface is null)
        {
            _typeface = SKTypeface.FromFile(_fontPath)
                ?? throw new InvalidOperationException($"Failed to parse Caveat handwriting font at '{_fontPath}'.");
        }

        return _typeface;
    }
}
