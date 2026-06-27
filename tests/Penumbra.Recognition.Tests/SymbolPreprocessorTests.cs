using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// 5b de-risking: the geometry features mirror crohme.py geom_features, and the sibling context
/// actually changes rel_height / y_position — the signal segmentation unlocks. Tests pass identity
/// mean/std so ComputeFeatures returns the raw (pre-standardization) values for exact assertions.
/// </summary>
public sealed class SymbolPreprocessorTests
{
    private static readonly float[] Mean0 = new float[SymbolPreprocessor.FeatureCount];
    private static readonly float[] Std1 = Enumerable.Repeat(1f, SymbolPreprocessor.FeatureCount).ToArray();

    private static float[] Raw(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
        SymbolPreprocessor.ComputeFeatures(strokes, context, Mean0, Std1);

    [Fact]
    public void Features_MatchFormula_ForKnownBox()
    {
        Stroke s = Box(0, 0, 2, 4);   // 2 wide, 4 tall
        float[] f = Raw(new[] { s }, new SymbolContext(RefHeight: 4, ExprYMin: 0, ExprHeight: 4));

        Assert.Equal((2 - 4) / (2 + 4 + 1e-6), f[0], 3);  // aspect (tall → negative)
        Assert.Equal(1.0, f[1], 3);                        // rel_height = h/ref = 4/4
        Assert.Equal(0.5, f[2], 3);                        // rel_width  = w/ref = 2/4
        Assert.Equal(0.5, f[3], 3);                        // y_position, centered in its own line
        Assert.Equal(1.0, f[4], 3);                        // stroke_count
    }

    [Fact]
    public void RelHeight_ShrinksWhenSiblingsAreTaller()
    {
        Stroke s = Box(0, 0, 2, 4);

        float alone = Raw(new[] { s }, new SymbolContext(4, 0, 4))[1];
        float amongTall = Raw(new[] { s }, new SymbolContext(8, 0, 8))[1];

        Assert.Equal(1.0, alone, 3);
        Assert.Equal(0.5, amongTall, 3);   // the same glyph reads as half-height next to tall neighbours
    }

    [Fact]
    public void YPosition_ReflectsPlaceInLine()
    {
        Stroke s = Box(0, 0, 2, 2);   // cy = 1, sitting at the top of a tall line
        float[] f = Raw(new[] { s }, new SymbolContext(RefHeight: 2, ExprYMin: 0, ExprHeight: 10));

        Assert.Equal(1.0 / 10.0, f[3], 3);   // (cy - ymin) / exprH = 1/10
    }

    [Fact]
    public void Standardization_AppliesMeanAndStd()
    {
        var strokes = new[] { Box(0, 0, 2, 4) };
        var context = new SymbolContext(RefHeight: 4, ExprYMin: 0, ExprHeight: 4);

        float[] raw = SymbolPreprocessor.ComputeFeatures(strokes, context, Mean0, Std1);
        float[] mean = { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f };
        float[] std = { 2f, 0.5f, 4f, 0.25f, 1.5f };
        float[] standardized = SymbolPreprocessor.ComputeFeatures(strokes, context, mean, std);

        for (int i = 0; i < SymbolPreprocessor.FeatureCount; i++)
        {
            Assert.Equal((raw[i] - mean[i]) / std[i], standardized[i], 3);   // (raw - mean) / std, per index
        }
    }

    private static Stroke Box(double x, double y, double w, double h) =>
        new(Guid.NewGuid(), new List<StrokeSample>
        {
            new(x, y, TimeSpan.Zero, 0.5),
            new(x + w, y + h, TimeSpan.Zero, 0.5),
        });
}
