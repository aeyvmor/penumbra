using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class BankGlyphSourceTests
{
    private static Stroke RawStroke(params (double x, double y)[] pts)
    {
        var samples = new StrokeSample[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            samples[i] = new StrokeSample(pts[i].x, pts[i].y, TimeSpan.FromMilliseconds(i * 10), 0.5);
        }

        return new Stroke(Guid.NewGuid(), samples);
    }

    [Fact]
    public void ReturnsEmBoxNormalizedStrokesFromBank()
    {
        string dir = Path.Combine(Path.GetTempPath(), "penumbra-bankglyph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bank = new JsonGlyphBank(Path.Combine(dir, "glyphs.json"));
            // Raw ink far from the origin and larger than a unit box.
            bank.Capture(new GlyphSample("7", new[] { RawStroke((100, 200), (140, 200), (120, 260)) }, DateTimeOffset.UtcNow));

            var source = new BankGlyphSource(bank);
            IReadOnlyList<Stroke>? glyph = source.GetGlyph("7", new Random(1));

            Assert.NotNull(glyph);
            double minX = glyph!.SelectMany(s => s.Samples).Min(p => p.X);
            double minY = glyph.SelectMany(s => s.Samples).Min(p => p.Y);
            double maxX = glyph.SelectMany(s => s.Samples).Max(p => p.X);
            double maxY = glyph.SelectMany(s => s.Samples).Max(p => p.Y);

            // Normalized into the unit em-box, with the major axis spanning exactly 1.
            Assert.InRange(minX, 0.0, 1.0);
            Assert.InRange(minY, 0.0, 1.0);
            Assert.InRange(maxX, 0.0, 1.0);
            Assert.InRange(maxY, 0.0, 1.0);
            Assert.Equal(1.0, Math.Max(maxX - minX, maxY - minY), precision: 9);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void ReturnsNullForUnknownSymbol()
    {
        string dir = Path.Combine(Path.GetTempPath(), "penumbra-bankglyph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bank = new JsonGlyphBank(Path.Combine(dir, "glyphs.json"));
            var source = new BankGlyphSource(bank);

            Assert.Null(source.GetGlyph("7", new Random(1))); // empty bank has nothing
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
