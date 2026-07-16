using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 5.5 slice 5: partition repair (<see cref="LineClustering"/>'s structural-neighbour merge pass, run
/// through the public <see cref="RegionSegmenter"/> entry point since <c>LineClustering</c> itself is
/// internal). Classification hasn't happened yet at this stage — every fixture here is pure geometry:
/// a bar-like (wide, flat) provisional line bridging a neighbour's content, or an unusually tall group
/// overlapping one, merges; anything else stays exactly as the plain Y-projection split it.
/// </summary>
public sealed class PartitionRepairTests
{
    private readonly RegionSegmenter _segmenter = new(new OverlapStrokeSegmenter());

    [Fact]
    public void StackedFractionShape_NumeratorBarDenominator_MergesIntoOneRegion()
    {
        // Three provisional Y-projection lines (gaps well past the 0.7x-median line-split threshold) that
        // together look like a stacked fraction: wide/flat bar bridging content above and below it.
        Stroke[] numerator =
        {
            Line(0, 0, 8, 20), Line(25, 0, 33, 20), Line(50, 0, 58, 20),
        };
        Stroke bar = Line(0, 35, 60, 37);
        Stroke denominator = Line(25, 52, 33, 72);

        InkSegmentation segmentation = _segmenter.Segment(
            numerator.Append(bar).Append(denominator).ToArray());

        InkRegion region = Assert.Single(segmentation.Regions);
        Assert.Equal(5, region.StrokeIds.Count);
        Assert.Equal(5, region.Groups.Count);
    }

    [Fact]
    public void TallMarkOverlappingANeighbourLine_MergesIntoOneRegion()
    {
        // An unusually tall group (well over the page's median symbol height) X-overlapping the line right
        // below it, split apart by a real vertical gap — the radical-envelope repair case.
        Stroke tallMark = Line(0, 0, 15, 55);             // height 55, e.g. a radical sign scaled tall
        Stroke content = Line(2, 90, 13, 110);            // height 20, the line right below it

        InkSegmentation segmentation = _segmenter.Segment(new[] { tallMark, content });

        InkRegion region = Assert.Single(segmentation.Regions);
        Assert.Equal(2, region.StrokeIds.Count);
    }

    [Fact]
    public void IndependentMultiLinePage_NoStructuralBridge_StaysThreeSeparateRegions()
    {
        // a=2 / b=1 / y=ax+b: three ordinary short lines, generously separated, with nothing bar-like or
        // unusually tall between any pair. Partition repair must leave these exactly as three regions —
        // this is the multi-line-dependency-page invariant the mandate calls out explicitly.
        Stroke[] line1 = { Line(0, 0, 10, 20), Line(20, 5, 30, 15), Line(40, 0, 50, 20) };       // a = 2
        Stroke[] line2 = { Line(0, 100, 10, 120), Line(20, 105, 30, 115), Line(40, 100, 50, 120) }; // b = 1
        Stroke[] line3 =
        {
            Line(0, 200, 10, 220), Line(20, 205, 30, 215), Line(40, 200, 50, 220),
            Line(60, 200, 70, 220), Line(80, 200, 90, 220),
        };                                                                                        // y = a x + b

        InkSegmentation segmentation = _segmenter.Segment(line1.Concat(line2).Concat(line3).ToArray());

        Assert.Equal(3, segmentation.Regions.Count);
    }

    [Fact]
    public void TwoOrdinaryLinesOfDifferentSymbolSize_NoBridgeOrEnvelope_StaySeparate()
    {
        // Regression guard for the merge heuristics themselves: one line of small ink above one line of
        // much taller ink (legitimately different handwriting scale), far enough apart to split, but with
        // no bar-like or unusually-tall-relative-to-median group between them — must stay two regions.
        Stroke[] small = { Line(0, 0, 10, 10), Line(20, 0, 30, 10) };            // height 10
        Stroke[] large = { Line(0, 200, 10, 240), Line(20, 200, 30, 240) };      // height 40

        InkSegmentation segmentation = _segmenter.Segment(small.Concat(large).ToArray());

        Assert.Equal(2, segmentation.Regions.Count);
    }

    [Fact]
    public void IndependentLineContainingWideMinus_DoesNotMergeWithLineBelow()
    {
        // A normal algebra line can contain a wide, flat subtraction stroke. One neighbour below is not
        // enough evidence for a fraction: the bar must bridge content on BOTH vertical sides.
        Stroke[] upper =
        {
            Line(0, 0, 10, 20), Line(20, 9, 65, 10), Line(75, 0, 85, 20),
        };
        Stroke[] lower =
        {
            Line(5, 100, 15, 120), Line(30, 100, 40, 120), Line(55, 100, 65, 120),
        };

        InkSegmentation segmentation = _segmenter.Segment(upper.Concat(lower).ToArray());

        Assert.Equal(2, segmentation.Regions.Count);
    }

    [Fact]
    public void ThreeCloseIndependentLines_WithMiddleSubtraction_StaySeparate()
    {
        Stroke[] upper = { Line(0, 0, 10, 20), Line(35, 0, 45, 20) };
        Stroke[] middle =
        {
            Line(0, 35, 10, 55), Line(15, 44, 50, 45), Line(55, 35, 65, 55),
        };
        Stroke[] lower = { Line(0, 70, 10, 90), Line(35, 70, 45, 90) };

        InkSegmentation segmentation = _segmenter.Segment(
            upper.Concat(middle).Concat(lower).ToArray());

        Assert.Equal(3, segmentation.Regions.Count);
    }

    [Fact]
    public void CloseStackedSameColumnGlyphs_WithoutStructuralBridge_StaySeparateRegions()
    {
        // User diagnostic pages often put one sample directly below the previous sample. Their clear gap
        // can be just under the broad baseline-jitter threshold, but near-total X overlap proves these are
        // separate rows rather than two symbols progressing left-to-right on one expression line.
        Stroke[] upper = { Line(0, 0, 40, 40), Line(40, 0, 0, 40) };
        Stroke[] lower = { Line(0, 63, 40, 103), Line(40, 63, 0, 103) };

        InkSegmentation segmentation = _segmenter.Segment(upper.Concat(lower).ToArray());

        Assert.Equal(2, segmentation.Regions.Count);
        Assert.All(segmentation.Regions, region => Assert.Equal(2, region.StrokeIds.Count));
    }

    [Fact]
    public void ClearSuperscriptOffsetToTheRight_RemainsWithItsBaseRegion()
    {
        Stroke[] baseline = { Line(0, 30, 40, 70), Line(40, 30, 0, 70) };
        Stroke[] exponent = { Line(35, 0, 55, 20), Line(55, 0, 35, 20) };

        InkSegmentation segmentation = _segmenter.Segment(baseline.Concat(exponent).ToArray());

        InkRegion region = Assert.Single(segmentation.Regions);
        Assert.Equal(4, region.StrokeIds.Count);
    }

    [Fact]
    public void StackedFraction_StableRegionId_SurvivesAnUnrelatedEditElsewhere()
    {
        // Region ids for a merged structural line must remain stable across an incremental pass, exactly
        // like any other region — the mandate's "stable stroke-set region IDs preserved" requirement.
        Stroke[] numerator =
        {
            Line(0, 0, 8, 20), Line(25, 0, 33, 20), Line(50, 0, 58, 20),
        };
        Stroke bar = Line(0, 35, 60, 37);
        Stroke denominator = Line(25, 52, 33, 72);
        Stroke[] first = numerator.Append(bar).Append(denominator).ToArray();

        InkSegmentation firstPass = _segmenter.Segment(first);
        Guid fractionRegionId = Assert.Single(firstPass.Regions).Id;

        Stroke unrelated = Line(0, 400, 10, 420);
        InkSegmentation secondPass = _segmenter.Segment(first.Append(unrelated).ToArray(), firstPass);

        Assert.Equal(2, secondPass.Regions.Count);
        InkRegion survivor = secondPass.Regions.Single(r => r.StrokeIds.Count == 5);
        Assert.Equal(fractionRegionId, survivor.Id);
    }

    [Fact]
    public void StackedFraction_TraversesPartitionClassificationAndRecursiveParser()
    {
        Stroke x = Line(0, 0, 8, 20);
        Stroke plusHorizontal = Line(19, 10, 29, 10);
        Stroke plusVertical = Line(24, 5, 24, 15);
        Stroke one = Line(42, 0, 50, 20);
        Stroke bar = Line(0, 35, 52, 37);
        Stroke two = Line(21, 52, 29, 72);
        Stroke[] ink = { x, plusHorizontal, plusVertical, one, bar, two };

        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(), new FractionGeometryClassifier());

        RegionRecognition region = Assert.Single(recognizer.RecognizeRegions(ink));

        Assert.True(region.Result.ParseOutcome?.IsAccepted, region.Result.ParseOutcome?.Detail);
        Assert.Equal(@"\frac{x+1}{2}", region.Result.Latex);
        Assert.Equal(6, region.Result.Tokens.Sum(token => token.SourceStrokeIds.Count));
    }

    [Fact]
    public void CloseWrittenFraction_SeparatesBeforeClassificationAndParsesRecursively()
    {
        // The user-captured failure shape: both vertical gaps are inside the ordinary same-symbol merge
        // radius, so the pre-fix segmenter sent all three strokes to the CNN as one division-like glyph.
        Stroke numerator = Line(0, 0, 15, 60);
        Stroke bar = Line(0, 68, 100, 68);
        Stroke denominator = Line(40, 76, 60, 136);
        Stroke[] ink = { numerator, bar, denominator };
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(), new FractionGeometryClassifier());

        RegionRecognition region = Assert.Single(recognizer.RecognizeRegions(ink));

        Assert.Equal(3, region.Region.Groups.Count);
        Assert.True(region.Result.ParseOutcome?.IsAccepted, region.Result.ParseOutcome?.Detail);
        Assert.Equal(@"\frac{x}{2}", region.Result.Latex);
        Assert.Equal(3, region.Result.Tokens.Sum(token => token.SourceStrokeIds.Count));
    }

    [Fact]
    public void MultiLineDependencies_A2_B1_YAxPlusB_StayThreeRecognizedRegions()
    {
        Stroke[] line1 = SymbolsAt(y: 0, count: 3);
        Stroke[] line2 = SymbolsAt(y: 100, count: 3);
        Stroke[] line3 = SymbolsAt(y: 200, count: 6);
        Stroke[] page = line1.Concat(line2).Concat(line3).ToArray();
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(), new MultiLineGeometryClassifier());

        IReadOnlyList<RegionRecognition> regions = recognizer.RecognizeRegions(page);

        Assert.Equal(3, regions.Count);
        Assert.Equal(new[] { "a=2", "b=1", "y=ax+b" }, regions.Select(region => region.Result.Latex));
        Assert.All(regions, region => Assert.True(region.Result.ParseOutcome?.IsAccepted));
    }

    private sealed class FractionGeometryClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            InkBounds bounds = SymbolPreprocessor.Bounds(strokes);
            string label = bounds.Y switch
            {
                _ when bounds.Width >= 40 && bounds.Height <= 4 => "-",
                >= 45 => "2",
                _ when strokes.Count == 2 => "+",
                _ when bounds.X < 15 => "x",
                _ => "1",
            };
            return new SymbolPrediction(label, 1.0);
        }
    }

    private sealed class MultiLineGeometryClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            InkBounds bounds = SymbolPreprocessor.Bounds(strokes);
            int column = (int)Math.Round(bounds.X / 20.0);
            string[] labels = bounds.Y switch
            {
                < 50 => new[] { "a", "=", "2" },
                < 150 => new[] { "b", "=", "1" },
                _ => new[] { "y", "=", "a", "x", "+", "b" },
            };
            return new SymbolPrediction(labels[column], 1.0);
        }
    }

    private static Stroke[] SymbolsAt(double y, int count) => Enumerable.Range(0, count)
        .Select(index => Line(index * 20, y, index * 20 + 8, y + 20))
        .ToArray();

    // A straight stroke from (x1,y1) to (x2,y2); only its bounding box drives segmentation/clustering.
    private static Stroke Line(double x1, double y1, double x2, double y2)
    {
        const int n = 8;
        var samples = new List<StrokeSample>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            samples.Add(new StrokeSample(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, TimeSpan.Zero, 0.5));
        }

        return new Stroke(Guid.NewGuid(), samples);
    }
}
