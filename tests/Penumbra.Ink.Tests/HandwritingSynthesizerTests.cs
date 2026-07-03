using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class HandwritingSynthesizerTests
{
    private static readonly SynthesisOptions Options = new(); // defaults; tests reference its fields, not literals

    private static readonly InkBounds Anchor = new(X: 100, Y: 40, Width: 20, Height: 30);

    // --- stub glyph sources (hand-authored em-box polylines, all sample times zero → paceless) ---

    private static Stroke EmStroke(params (double x, double y)[] pts)
    {
        var samples = new StrokeSample[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            samples[i] = new StrokeSample(pts[i].x, pts[i].y, TimeSpan.Zero, 0.5);
        }

        return new Stroke(Guid.NewGuid(), samples);
    }

    // A single-stroke "2"-like glyph spanning most of the em box (3 samples).
    private static IReadOnlyList<Stroke> Glyph2() => new[]
    {
        EmStroke((0.2, 0.1), (0.8, 0.5), (0.2, 0.9)),
    };

    // A single-stroke "5"-like glyph, distinct from Glyph2 (5 samples).
    private static IReadOnlyList<Stroke> Glyph5() => new[]
    {
        EmStroke((0.8, 0.1), (0.2, 0.1), (0.2, 0.5), (0.8, 0.6), (0.2, 0.9)),
    };

    private sealed class StubSource : IGlyphSource
    {
        private readonly IReadOnlyDictionary<string, Func<IReadOnlyList<Stroke>>> _glyphs;
        public int Queries { get; private set; }

        public StubSource(IReadOnlyDictionary<string, Func<IReadOnlyList<Stroke>>> glyphs) => _glyphs = glyphs;

        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random)
        {
            Queries++;
            return _glyphs.TryGetValue(symbol, out Func<IReadOnlyList<Stroke>>? make) ? make() : null;
        }
    }

    private static HandwritingSynthesizer WithGlyphs(params (string sym, Func<IReadOnlyList<Stroke>> make)[] glyphs)
    {
        var map = glyphs.ToDictionary(g => g.sym, g => g.make);
        return new HandwritingSynthesizer(new IGlyphSource[] { new StubSource(map) });
    }

    private static double MinX(Stroke s) => s.Samples.Min(p => p.X);
    private static double MaxX(Stroke s) => s.Samples.Max(p => p.X);
    private static double MinY(Stroke s) => s.Samples.Min(p => p.Y);
    private static double MaxY(Stroke s) => s.Samples.Max(p => p.Y);

    // --- determinism ---

    [Fact]
    public void SameSeedProducesByteIdenticalGeometry()
    {
        var synth = WithGlyphs(("2", Glyph2), ("5", Glyph5));

        SynthesizedHandwriting a = synth.Synthesize("25 2", Anchor, Options, new Random(12345))!;
        SynthesizedHandwriting b = synth.Synthesize("25 2", Anchor, Options, new Random(12345))!;

        Assert.Equal(a.Strokes.Count, b.Strokes.Count);
        for (int i = 0; i < a.Strokes.Count; i++)
        {
            // Ids differ (fresh Guids); geometry must match sample-for-sample.
            Assert.Equal(a.Strokes[i].Samples, b.Strokes[i].Samples);
        }

        Assert.Equal(a.Timeline.TotalDuration, b.Timeline.TotalDuration);
    }

    // --- jitter ---

    [Fact]
    public void RepeatedDigitsDifferInShapeButStayInBounds()
    {
        var synth = WithGlyphs(("2", Glyph2));
        SynthesizedHandwriting r = synth.Synthesize("22", Anchor, Options, new Random(7))!;

        Assert.Equal(2, r.Strokes.Count);

        // Compare SHAPE (offset each glyph to its own min corner) so layout translation isn't what differs.
        StrokeSample[] shape0 = Shape(r.Strokes[0]);
        StrokeSample[] shape1 = Shape(r.Strokes[1]);
        Assert.NotEqual(shape0, shape1); // affine jitter drew different scale/rotation/translation

        // Both glyphs sit within a sane slot: ~one line height wide, vertically near the anchor centre.
        double centerY = Anchor.Y + Anchor.Height / 2.0;
        foreach (Stroke s in r.Strokes)
        {
            Assert.True(MaxX(s) - MinX(s) <= Options.LineHeight * 1.3);
            Assert.InRange((MinY(s) + MaxY(s)) / 2.0, centerY - Options.LineHeight * 0.2, centerY + Options.LineHeight * 0.2);
        }
    }

    private static StrokeSample[] Shape(Stroke s)
    {
        double minX = MinX(s), minY = MinY(s);
        return s.Samples.Select(p => p with { X = p.X - minX, Y = p.Y - minY }).ToArray();
    }

    // --- layout ---

    [Fact]
    public void FirstGlyphHonoursAnchorGapAndGlyphsAdvanceMonotonically()
    {
        var synth = WithGlyphs(("2", Glyph2), ("5", Glyph5));
        SynthesizedHandwriting r = synth.Synthesize("252", Anchor, Options, new Random(99))!;

        Assert.Equal(3, r.Strokes.Count); // one stroke per glyph

        // First glyph's left ink edge lands exactly on anchor.Right + gap.
        double expectedLeft = Anchor.X + Anchor.Width + Options.GapAfterAnchor * Options.LineHeight;
        Assert.Equal(expectedLeft, MinX(r.Strokes[0]), precision: 9);

        // Each subsequent glyph starts strictly right of the previous glyph's right ink edge (monotonic + gap).
        for (int i = 1; i < r.Strokes.Count; i++)
        {
            Assert.True(MinX(r.Strokes[i]) > MaxX(r.Strokes[i - 1]));
        }
    }

    [Fact]
    public void GlyphIsVerticallyCentredOnAnchor()
    {
        var synth = WithGlyphs(("2", Glyph2));
        SynthesizedHandwriting r = synth.Synthesize("2", Anchor, Options, new Random(3))!;

        double centerY = Anchor.Y + Anchor.Height / 2.0;
        Stroke glyph = Assert.Single(r.Strokes);
        // Glyph2 is symmetric top-to-bottom about em y=0.5, so its ink centre maps onto the anchor centre
        // (within the small translation jitter).
        Assert.InRange((MinY(glyph) + MaxY(glyph)) / 2.0, centerY - Options.LineHeight * 0.1, centerY + Options.LineHeight * 0.1);
    }

    [Fact]
    public void NarrowGlyphAdvancesLessThanWideGlyph()
    {
        // A '1'-like glyph occupies little horizontal ink; aspect-preserving em-box keeps it narrow, so the
        // pen advances less than for a full-width glyph.
        IReadOnlyList<Stroke> Narrow() => new[] { EmStroke((0.5, 0.0), (0.52, 0.5), (0.5, 1.0)) };
        var synth = WithGlyphs(("1", Narrow), ("2", Glyph2));

        var seed = 4;
        SynthesizedHandwriting narrow = synth.Synthesize("11", Anchor, Options, new Random(seed))!;
        SynthesizedHandwriting wide = synth.Synthesize("22", Anchor, Options, new Random(seed))!;

        double narrowGap = MinX(narrow.Strokes[1]) - MinX(narrow.Strokes[0]);
        double wideGap = MinX(wide.Strokes[1]) - MinX(wide.Strokes[0]);
        Assert.True(narrowGap < wideGap);
    }

    // --- space ---

    [Fact]
    public void SpaceAdvancesPenByHalfEmWithoutInk()
    {
        var synth = WithGlyphs(("2", Glyph2));

        int seed = 55;
        SynthesizedHandwriting spaced = synth.Synthesize("2 2", Anchor, Options, new Random(seed))!;
        SynthesizedHandwriting tight = synth.Synthesize("22", Anchor, Options, new Random(seed))!;

        // Space produces no stroke: both render exactly two glyph strokes.
        Assert.Equal(2, spaced.Strokes.Count);
        Assert.Equal(2, tight.Strokes.Count);

        // Same seed ⇒ identical jitter; the only difference is the space's advance on the second glyph.
        double delta = MinX(spaced.Strokes[1]) - MinX(tight.Strokes[1]);
        Assert.Equal(Options.SpaceAdvance * Options.LineHeight, delta, precision: 9);
    }

    // --- miss handling & priority chain ---

    [Fact]
    public void UnknownSymbolIsRecordedAndKnownGlyphsStillRender()
    {
        var synth = WithGlyphs(("2", Glyph2));
        SynthesizedHandwriting r = synth.Synthesize("2?", Anchor, Options, new Random(1))!;

        Assert.Single(r.Strokes);                       // only the "2" produced ink
        Assert.Equal(new[] { "?" }, r.MissingSymbols);  // the miss was recorded
    }

    [Fact]
    public void FirstSourceWinsAndSecondIsConsultedOnlyOnMiss()
    {
        // Source A supplies "2" (3-sample glyph); source B supplies "2" (5-sample) and "5".
        var a = new StubSource(new Dictionary<string, Func<IReadOnlyList<Stroke>>> { ["2"] = Glyph2 });
        var b = new StubSource(new Dictionary<string, Func<IReadOnlyList<Stroke>>> { ["2"] = Glyph5, ["5"] = Glyph5 });
        var synth = new HandwritingSynthesizer(new IGlyphSource[] { a, b });

        SynthesizedHandwriting r = synth.Synthesize("25", Anchor, Options, new Random(2))!;

        // "2" resolved from A (3 samples), not B (5). Then "5" only exists in B.
        Assert.Equal(3, r.Strokes[0].Samples.Count);
        Assert.Equal(5, r.Strokes[1].Samples.Count);

        // B is consulted once — for the "5" that A lacks (A is asked for both, B only for the second).
        Assert.Equal(2, a.Queries);
        Assert.Equal(1, b.Queries);
    }

    [Fact]
    public void AllGlyphsMissingReturnsNonNullEmptyInkWithMissingSymbols()
    {
        var synth = WithGlyphs(("2", Glyph2)); // knows only "2"
        SynthesizedHandwriting? r = synth.Synthesize("ab", Anchor, Options, new Random(1));

        Assert.NotNull(r); // non-null so the caller still learns what to typeset
        Assert.Empty(r!.Strokes);
        Assert.Equal(TimeSpan.Zero, r.Timeline.TotalDuration);
        Assert.Equal(new[] { "a", "b" }, r.MissingSymbols);
    }

    [Fact]
    public void EmptyOrWhitespaceTextReturnsNull()
    {
        var synth = WithGlyphs(("2", Glyph2));
        Assert.Null(synth.Synthesize("", Anchor, Options, new Random(1)));
        Assert.Null(synth.Synthesize("   ", Anchor, Options, new Random(1)));
    }

    [Fact]
    public void SymbolMapTranslatesDisplayCharsToLabels()
    {
        // '×' must be looked up under its LaTeX label, not the raw char.
        var synth = WithGlyphs(("\\times", Glyph2));
        SynthesizedHandwriting r = synth.Synthesize("×", Anchor, Options, new Random(1))!;

        Assert.Single(r.Strokes);
        Assert.Empty(r.MissingSymbols);
    }

    // --- re-timing ---

    [Fact]
    public void PacelessGlyphIsRetimedByArcLengthAtPenSpeed()
    {
        var synth = WithGlyphs(("2", Glyph2));
        SynthesizedHandwriting r = synth.Synthesize("2", Anchor, Options, new Random(0))!;

        Stroke glyph = Assert.Single(r.Strokes);
        TimeSpan duration = glyph.Samples[^1].Time - glyph.Samples[0].Time;

        // Positive and consistent with world arc length / pen speed.
        Assert.True(duration > TimeSpan.Zero);
        double arc = 0;
        for (int i = 1; i < glyph.Samples.Count; i++)
        {
            double dx = glyph.Samples[i].X - glyph.Samples[i - 1].X;
            double dy = glyph.Samples[i].Y - glyph.Samples[i - 1].Y;
            arc += Math.Sqrt(dx * dx + dy * dy);
        }

        Assert.Equal(arc / Options.PenSpeed, duration.TotalSeconds, precision: 6);
    }

    [Fact]
    public void RealCapturedPaceIsPreserved()
    {
        // A glyph whose samples carry distinct, increasing times must keep them (not be re-timed).
        IReadOnlyList<Stroke> Paced() => new[]
        {
            new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(0.2, 0.1, TimeSpan.FromMilliseconds(0), 0.5),
                new StrokeSample(0.8, 0.5, TimeSpan.FromMilliseconds(200), 0.5),
                new StrokeSample(0.2, 0.9, TimeSpan.FromMilliseconds(500), 0.5),
            }),
        };
        var synth = WithGlyphs(("Z", Paced));
        SynthesizedHandwriting r = synth.Synthesize("Z", Anchor, Options, new Random(0))!;

        Stroke glyph = Assert.Single(r.Strokes);
        // Captured cadence 0/200/500 ms survives layout unchanged.
        Assert.Equal(TimeSpan.FromMilliseconds(0), glyph.Samples[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(200), glyph.Samples[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(500), glyph.Samples[2].Time);
    }

    [Fact]
    public void TimelineTotalEqualsStrokeDurationsPlusAirMoves()
    {
        var synth = WithGlyphs(("2", Glyph2), ("5", Glyph5));
        SynthesizedHandwriting r = synth.Synthesize("25", Anchor, Options, new Random(8))!;

        Assert.True(r.Timeline.TotalDuration > TimeSpan.Zero);

        // Recompute the arithmetic the timeline should embody: Σ stroke durations + Σ clamped air moves.
        TimeSpan strokeTotal = TimeSpan.Zero;
        foreach (Stroke s in r.Strokes)
        {
            strokeTotal += s.Samples[^1].Time - s.Samples[0].Time;
        }

        TimeSpan airTotal = TimeSpan.Zero;
        for (int i = 1; i < r.Strokes.Count; i++)
        {
            double dx = r.Strokes[i].Samples[0].X - r.Strokes[i - 1].Samples[^1].X;
            double dy = r.Strokes[i].Samples[0].Y - r.Strokes[i - 1].Samples[^1].Y;
            var gap = TimeSpan.FromSeconds(Math.Sqrt(dx * dx + dy * dy) / Options.PenSpeed);
            if (gap < Options.MinAirMove) gap = Options.MinAirMove;
            else if (gap > Options.MaxAirMove) gap = Options.MaxAirMove;
            airTotal += gap;
        }

        Assert.Equal(strokeTotal + airTotal, r.Timeline.TotalDuration);
    }

    // --- non-mutation ---

    [Fact]
    public void SourceStrokesAreNotMutatedOrAliased()
    {
        IReadOnlyList<Stroke>? captured = null;
        var source = new DelegateSource(sym =>
        {
            captured = Glyph2();
            return captured;
        });
        var synth = new HandwritingSynthesizer(new IGlyphSource[] { source });

        SynthesizedHandwriting r = synth.Synthesize("2", Anchor, Options, new Random(1))!;

        // The synthesized ink must be new coordinates, never the em-box source aliased through.
        Assert.NotNull(captured);
        StrokeSample original = captured![0].Samples[0]; // still (0.2, 0.1) in em space
        Assert.Equal(0.2, original.X, precision: 9);
        Assert.Equal(0.1, original.Y, precision: 9);
        Assert.NotSame(captured[0], r.Strokes[0]);
    }

    private sealed class DelegateSource : IGlyphSource
    {
        private readonly Func<string, IReadOnlyList<Stroke>?> _get;
        public DelegateSource(Func<string, IReadOnlyList<Stroke>?> get) => _get = get;
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) => _get(symbol);
    }
}
