using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class CaveatGlyphSourceTests
{
    private const double Eps = 1e-9;

    // Resolve the real font by walking up from the test output dir to the repo root (tests run from bin/).
    private static string FontPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "assets", "Caveat-VariableFont_wght.ttf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate assets/Caveat-VariableFont_wght.ttf above the test output dir.");
    }

    private static CaveatGlyphSource Source() => new(FontPath());

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("6")]
    [InlineData("7")]
    [InlineData("8")]
    [InlineData("9")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("(")]
    [InlineData(")")]
    public void ExtractsCommonSymbolsAsNormalizedStrokes(string symbol)
    {
        IReadOnlyList<Stroke>? glyph = Source().GetGlyph(symbol, new Random(1));

        Assert.NotNull(glyph);
        Assert.NotEmpty(glyph!);

        int sampleCount = 0;
        foreach (Stroke stroke in glyph!)
        {
            foreach (StrokeSample s in stroke.Samples)
            {
                sampleCount++;
                Assert.InRange(s.X, -Eps, 1.0 + Eps);
                Assert.InRange(s.Y, -Eps, 1.0 + Eps);
            }
        }

        Assert.True(sampleCount >= 8, $"expected >= 8 samples, got {sampleCount}");
    }

    [Fact]
    public void AllSampleTimesAreZero()
    {
        IReadOnlyList<Stroke>? glyph = Source().GetGlyph("8", new Random(1));

        Assert.NotNull(glyph);
        Assert.All(glyph!.SelectMany(s => s.Samples), s => Assert.Equal(TimeSpan.Zero, s.Time));
    }

    [Fact]
    public void IsDeterministicAcrossCalls()
    {
        var source = Source();
        IReadOnlyList<Stroke>? first = source.GetGlyph("5", new Random(7));
        IReadOnlyList<Stroke>? second = source.GetGlyph("5", new Random(99)); // different seed, same geometry

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Count, second!.Count);
        for (int i = 0; i < first.Count; i++)
        {
            IReadOnlyList<StrokeSample> a = first[i].Samples;
            IReadOnlyList<StrokeSample> b = second[i].Samples;
            Assert.Equal(a.Count, b.Count);
            for (int j = 0; j < a.Count; j++)
            {
                Assert.Equal(a[j].X, b[j].X);
                Assert.Equal(a[j].Y, b[j].Y);
            }
        }
    }

    [Fact]
    public void MapsLatexTimesLabel()
    {
        IReadOnlyList<Stroke>? glyph = Source().GetGlyph("\\times", new Random(1));

        Assert.NotNull(glyph);
        Assert.NotEmpty(glyph!);
    }

    [Fact]
    public void ReturnsNullForUnknownMultiCharSymbol()
    {
        Assert.Null(Source().GetGlyph("\\notarealthing", new Random(1)));
    }

    [Fact]
    public void ReturnsNullForCharTheFontLacks()
    {
        // U+2603 SNOWMAN is not in this Latin handwriting font, so it maps to .notdef (glyph 0) → honest null.
        Assert.Null(Source().GetGlyph("☃", new Random(1)));
    }

    [Fact]
    public void MissingFontFileThrowsAtConstruction()
    {
        string absent = Path.Combine(Path.GetTempPath(), "penumbra-no-such-font-" + Guid.NewGuid().ToString("N") + ".ttf");
        Assert.Throws<FileNotFoundException>(() => new CaveatGlyphSource(absent));
    }

    [Fact]
    public void SynthesizesFromEmptyBankPlusFont_ColdStartWorks()
    {
        // Bank first (empty → always declines), Caveat font behind it: proves cold-start M2 from zero glyphs.
        var chain = new List<IGlyphSource>
        {
            new BankGlyphSource(new EmptyGlyphBank()),
            Source(),
        };
        var synth = new HandwritingSynthesizer(chain);

        var anchor = new InkBounds(0, 0, 10, 48);
        SynthesizedHandwriting? result = synth.Synthesize("28", anchor, new SynthesisOptions(), new Random(1));

        Assert.NotNull(result);
        Assert.Empty(result!.MissingSymbols);
        Assert.NotEmpty(result.Strokes);
        Assert.True(result.Timeline.TotalDuration > TimeSpan.Zero, "expected a positive animation duration");
    }

    /// <summary>An IGlyphBank that never holds anything — the cold-start test's stand-in for a fresh user.</summary>
    private sealed class EmptyGlyphBank : IGlyphBank
    {
        public void Capture(GlyphSample sample) { }

        public bool Has(string symbol) => false;

        public IReadOnlyList<GlyphSample> Samples(string symbol) => Array.Empty<GlyphSample>();

        public GlyphSample? Sample(string symbol, Random random) => null;
    }
}
