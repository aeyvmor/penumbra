using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 5.5 slice 5: the radical split hypothesis end to end through <see cref="ExpressionRecognizer"/>
/// (<c>RadicalSplitHypothesis</c> itself is internal and geometry-only — these fixtures exercise it through
/// the public recognizer so the extra classify call, the commit/fall-back decision, and the final
/// <c>RadicalNode</c> parse all prove out together). <see cref="SegmenterRealInkRegressionTests"/> pins that
/// <see cref="OverlapStrokeSegmenter"/> fuses the pinned real-ink radical+radicand ink into one 4-stroke
/// group unchanged — this file proves what happens to that exact fused shape next.
/// </summary>
public sealed class RadicalSplitHypothesisTests
{
    [Fact]
    public void FusedRadicalRealInkShape_SplitsAndParsesAsSqrt9Equals()
    {
        // The exact pinned SegmenterRealInkRegressionTests.Shot2_Sqrt9Equals_... coordinates: tick, rise,
        // and vinculum strokes fuse with the "9" into one 4-stroke group; '=' stays a separate 2-stroke
        // group. The split hypothesis should find {tick,rise,vinculum} vs {9} (verified geometrically:
        // the 3-stroke leading subset starts at/above and spans across nearly all of the "9"'s width; a
        // 1- or 2-stroke leading subset does not).
        Stroke tick = Line(270, 285, 310, 360);
        Stroke rise = Line(310, 360, 335, 228);
        Stroke vinculum = Line(335, 228, 450, 232);
        Stroke nine = Line(340, 250, 400, 355);
        Stroke equalsTop = Line(490, 290, 560, 295);
        Stroke equalsBottom = Line(495, 320, 580, 328);

        var classifier = new RadicalShapeFakeClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        RecognitionResult result = recognizer.Recognize(
            new[] { tick, rise, vinculum, nine, equalsTop, equalsBottom });

        Assert.NotNull(result.ParseOutcome);
        Assert.True(result.ParseOutcome!.IsAccepted, result.ParseOutcome.Detail);
        Assert.Equal(@"\sqrt{9}=", result.Latex);

        // Ownership: all 6 original strokes are accounted for across the final token set (radical mark
        // owns 3, the radicand digit owns 1, the relation owns 2) — nothing lost, nothing double-owned.
        int totalOwnedStrokes = result.Tokens.Sum(t => t.SourceStrokeIds.Count);
        Assert.Equal(6, totalOwnedStrokes);
    }

    [Fact]
    public void FusedRadicalAfterYEquals_IgnoresEarlierYAndEqualsFalseEnvelopes()
    {
        // Dogfood: sqrt alone worked, but y=sqrt(9) read as y=pi. Both the two-stroke y and the stacked
        // equals bars satisfy the old loose "leading subset spans the rest" geometry, so the one bounded
        // probe was spent before reaching the genuine radical group.
        Stroke[] y = { Line(2, 5, 12, 15), Line(0, 0, 16, 32) };
        Stroke[] equals = { Line(35, 10, 55, 10), Line(35, 20, 55, 20) };
        Stroke[] fusedRadical =
        {
            Line(80, 57, 100, 95),
            Line(100, 95, 113, 29),
            Line(113, 29, 170, 31),
            Line(116, 40, 146, 92),
        };
        Stroke[] all = y.Concat(equals).Concat(fusedRadical).ToArray();
        var groups = new[]
        {
            new StrokeGroup(y, SymbolPreprocessor.Bounds(y)),
            new StrokeGroup(equals, SymbolPreprocessor.Bounds(equals)),
            new StrokeGroup(fusedRadical, SymbolPreprocessor.Bounds(fusedRadical)),
        };
        var region = new InkRegion(
            Guid.NewGuid(), all.Select(stroke => stroke.Id).ToArray(), SymbolPreprocessor.Bounds(all), groups);
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(), new PrefixedRadicalFakeClassifier());

        RecognitionResult result = recognizer.RecognizeRegion(region);

        Assert.True(result.ParseOutcome?.IsAccepted, result.ParseOutcome?.Detail);
        Assert.Equal(@"y=\sqrt{9}", result.Latex);
        Assert.Equal(all.Length, result.Tokens.Sum(token => token.SourceStrokeIds.Count));
    }

    [Fact]
    public void GeometricEnvelopeCandidate_LowConfidenceSqrtRead_FallsBackToNormalSingleSymbol()
    {
        // The two strokes' shape passes the geometric envelope/coverage test, but the classifier reads the
        // leading (candidate radical) subset as something other than a confident \sqrt — the split must
        // NOT be committed; the original two strokes stay one group, classified normally as a whole.
        Stroke candidate = Line(0, 0, 40, 5);     // wide, flat — geometrically "envelope-shaped"
        Stroke content = Line(10, 10, 30, 30);    // narrower, below

        var classifier = new LowConfidenceRadicalFakeClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        RecognitionResult result = recognizer.Recognize(new[] { candidate, content });

        RecognizedToken only = Assert.Single(result.Tokens);
        Assert.Equal("8", only.Latex);
        Assert.Equal(2, only.SourceStrokeIds.Count);   // both strokes stayed one unsplit symbol.
    }

    [Fact]
    public void MultipleGeometricEnvelopeCandidates_UseAtMostOneExtraClassifierBatch()
    {
        Stroke firstEnvelope = Line(0, 0, 40, 2);
        Stroke firstContent = Line(10, 8, 25, 25);
        Stroke secondEnvelope = Line(70, 0, 110, 2);
        Stroke secondContent = Line(80, 8, 95, 25);
        Stroke ordinary = Line(140, 0, 145, 25);

        var classifier = new BatchBudgetClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        _ = recognizer.Recognize(
            new[] { firstEnvelope, firstContent, secondEnvelope, secondContent, ordinary });

        Assert.InRange(classifier.BatchCalls, 1, 2); // normal batch + at most one bounded hypothesis batch.
    }

    [Fact]
    public void OversizedFusedGroup_SkipsTheSpeculativeClassifierBatch()
    {
        Stroke[] strokes = Enumerable.Range(0, 40)
            .Select(index => Line(index * 0.1, 0, 50 + index * 0.1, 20))
            .ToArray();
        var group = new StrokeGroup(strokes, SymbolPreprocessor.Bounds(strokes));
        var region = new InkRegion(
            Guid.NewGuid(), strokes.Select(stroke => stroke.Id).ToArray(), group.Bounds, new[] { group });
        var classifier = new BatchBudgetClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        _ = recognizer.RecognizeRegion(region);

        Assert.Equal(1, classifier.BatchCalls);
    }

    // Labels by stroke count, matching the specific shapes constructed above: 3 strokes (the radical mark
    // candidate) -> \sqrt; 2 strokes (the untouched '=' group) -> '='; 1 stroke (the radicand) -> '9'.
    private sealed class RadicalShapeFakeClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            strokes.Count switch
            {
                3 => new SymbolPrediction(@"\sqrt", 0.9),
                2 => new SymbolPrediction("=", 1.0),
                1 => new SymbolPrediction("9", 1.0),
                _ => new SymbolPrediction("?", 0.1, Rejected: true),
            };
    }

    private sealed class PrefixedRadicalFakeClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            InkBounds bounds = SymbolPreprocessor.Bounds(strokes);
            if (bounds.X < 30)
            {
                return new SymbolPrediction("y", 0.95);
            }

            if (bounds.X < 70)
            {
                return new SymbolPrediction("=", 0.99);
            }

            return strokes.Count switch
            {
                3 => new SymbolPrediction(@"\sqrt", 0.95),
                1 => new SymbolPrediction("9", 0.99),
                _ => new SymbolPrediction(@"\pi", 0.90),
            };
        }
    }

    // The hypothesis batch classifies the wide candidate stroke alone as a low-confidence non-sqrt label;
    // the untouched two-stroke group (normal fallback path) reads as '8'.
    private sealed class LowConfidenceRadicalFakeClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            if (strokes.Count == 2)
            {
                return new SymbolPrediction("8", 1.0);
            }

            InkBounds bounds = SymbolPreprocessor.Bounds(strokes);
            return bounds.Width >= 35
                ? new SymbolPrediction("1", 0.3)     // the envelope-shaped candidate: not a confident \sqrt.
                : new SymbolPrediction("3", 0.9);    // the radicand-shaped remainder: never used (discarded).
        }
    }

    private sealed class BatchBudgetClassifier : ISymbolClassifier
    {
        public int BatchCalls { get; private set; }

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            InkBounds bounds = SymbolPreprocessor.Bounds(strokes);
            // Only the leftmost geometric hypothesis is a radical; the second falls back to normal.
            if (strokes.Count == 1 && bounds.Width >= 35 && bounds.X < 50)
            {
                return new SymbolPrediction(@"\sqrt", 0.9);
            }

            return new SymbolPrediction("x", 0.9);
        }

        public IReadOnlyList<SymbolPrediction> ClassifyBatch(
            IReadOnlyList<IReadOnlyList<Stroke>> symbols, SymbolContext context)
        {
            BatchCalls++;
            return symbols.Select(strokes => Classify(strokes, context)).ToArray();
        }
    }

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
