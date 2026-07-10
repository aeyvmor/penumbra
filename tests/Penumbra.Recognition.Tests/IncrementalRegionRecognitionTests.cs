using Penumbra.Core;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// Phase 5a: per-region recognition + dirty tracking on <see cref="ExpressionRecognizer"/>. Proves the
/// context is computed per region (not per page), that editing one line re-reads only that line while
/// clean regions reuse their prior result verbatim, and — critically — that a single-line page reads
/// IDENTICALLY through the new region path as through the legacy page path, on the 3.9g real-ink shapes.
/// </summary>
public sealed class IncrementalRegionRecognitionTests
{
    // ---- single-line parity: the region path must not change existing single-line reads --------------

    [Fact]
    public void SingleLine_RealInk_RecognizeRegionsMatchesPageRecognize()
    {
        // 3.9g Shot9 '5-1=' coordinates (one line). The two paths must agree token-for-token.
        Stroke[] strokes =
        {
            Line(150, 275, 155, 400),   // 5 stem
            Line(150, 275, 290, 280),   // 5 top bar
            Line(355, 347, 390, 349),   // minus
            Line(410, 285, 445, 275),   // 1 flag
            Line(445, 275, 425, 390),   // 1 stem
            Line(490, 315, 555, 318),   // = top
            Line(490, 345, 580, 350),   // = bottom
        };

        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());

        RecognitionResult page = recognizer.Recognize(strokes);
        IReadOnlyList<RegionRecognition> regions = recognizer.RecognizeRegions(strokes);

        RegionRecognition only = Assert.Single(regions);
        Assert.True(only.Dirty);   // first pass: nothing to reuse
        Assert.Equal(page.Latex, only.Result.Latex);
        Assert.Equal(page.Confidence, only.Result.Confidence, 9);
        Assert.Equal(page.MinConfidence, only.Result.MinConfidence, 9);

        // Token-for-token, not count-for-count: Seam 1's stroke↔token alignment must survive the
        // region path — same labels, same source strokes, same boxes, same calibration verdicts.
        Assert.Equal(page.Tokens.Count, only.Result.Tokens.Count);
        for (int i = 0; i < page.Tokens.Count; i++)
        {
            RecognizedToken expected = page.Tokens[i];
            RecognizedToken actual = only.Result.Tokens[i];
            Assert.Equal(expected.Latex, actual.Latex);
            Assert.Equal(expected.SourceStrokeIds, actual.SourceStrokeIds);
            Assert.Equal(expected.Bounds, actual.Bounds);
            Assert.Equal(expected.Confidence, actual.Confidence, 9);
            Assert.Equal(expected.Rejected, actual.Rejected);
        }
    }

    [Fact]
    public void SingleLine_RealInk_27_RecognizeRegionMatchesPageRecognize()
    {
        // 3.9g Shot11 '27' — a second real-ink single-line shape, via the explicit region entry point.
        Stroke[] strokes =
        {
            Line(200, 260, 335, 380),   // 2
            Line(350, 235, 420, 240),   // 7 top bar
            Line(415, 232, 360, 425),   // 7 stem
        };

        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());
        RecognitionResult page = recognizer.Recognize(strokes);

        InkSegmentation seg = new RegionSegmenter(new OverlapStrokeSegmenter()).Segment(strokes);
        RecognitionResult region = recognizer.RecognizeRegion(Assert.Single(seg.Regions));

        Assert.Equal(page.Latex, region.Latex);
        Assert.Equal(page.Tokens.Count, region.Tokens.Count);
    }

    // ---- per-region context (not per-page) ----------------------------------------------------------

    [Fact]
    public void ContextIsPerRegion_EachLineJudgedAgainstItsOwnSiblings()
    {
        // Two lines with DIFFERENT symbol heights: a per-page context would hand both lines the same
        // ref height; a per-region context gives each line its own. The recorder proves the latter.
        var line1 = new[] { VBar(0, 0, 10), VBar(40, 0, 10) };       // 10px tall
        var line2 = new[] { VBar(0, 300, 40), VBar(40, 300, 40) };   // 40px tall, far below

        var classifier = new RecordingClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        recognizer.RecognizeRegions(line1.Concat(line2).ToList());

        // EVERY symbol got its own line's context — exactly two per line, with that line's full triple
        // (ref height AND vertical extent), so no symbol was judged against a blend, the page, or a
        // neighbouring line.
        Assert.Equal(4, classifier.Contexts.Count);
        Assert.Equal(2, classifier.Contexts.Count(c =>
            Math.Abs(c.RefHeight - 10.0) < 1e-6 && Math.Abs(c.ExprYMin - 0.0) < 1e-6
            && Math.Abs(c.ExprHeight - 10.0) < 1e-6));
        Assert.Equal(2, classifier.Contexts.Count(c =>
            Math.Abs(c.RefHeight - 40.0) < 1e-6 && Math.Abs(c.ExprYMin - 300.0) < 1e-6
            && Math.Abs(c.ExprHeight - 40.0) < 1e-6));
    }

    // ---- dirty tracking -----------------------------------------------------------------------------

    [Fact]
    public void FirstPass_EveryRegionIsDirty()
    {
        var strokes = new[]
        {
            VBar(0, 0, 20), VBar(40, 0, 20),
            VBar(0, 300, 20), VBar(40, 300, 20),
        };
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(strokes);

        Assert.Equal(2, first.Count);
        Assert.All(first, r => Assert.True(r.Dirty));
    }

    [Fact]
    public void EditingOneLine_DirtiesOnlyThatLine_AndReusesTheOther()
    {
        var line1 = new List<Stroke> { VBar(0, 0, 20), VBar(40, 0, 20) };
        var line2 = new List<Stroke> { VBar(0, 300, 20), VBar(40, 300, 20) };

        var classifier = new RecordingClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(line1.Concat(line2).ToList());
        int classifiedAfterFirst = classifier.SymbolsClassified;

        // Append a symbol to line 2 only.
        line2.Add(VBar(80, 300, 20));
        IReadOnlyList<RegionRecognition> second =
            recognizer.RecognizeRegions(line1.Concat(line2).ToList(), first);

        Assert.Equal(2, second.Count);
        Assert.False(second[0].Dirty);   // line 1 untouched
        Assert.True(second[1].Dirty);    // line 2 edited

        // The clean region's result is the SAME object reused, not a re-run.
        Assert.Same(first[0].Result, second[0].Result);

        // Only the 3 symbols of the edited line were (re)classified on the second pass.
        Assert.Equal(classifiedAfterFirst + 3, classifier.SymbolsClassified);
    }

    [Fact]
    public void RegionIdsStable_ThroughRecognizeRegions_UnderUnrelatedEdit()
    {
        var line1 = new List<Stroke> { VBar(0, 0, 20), VBar(40, 0, 20) };
        var line2 = new List<Stroke> { VBar(0, 300, 20), VBar(40, 300, 20) };
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(line1.Concat(line2).ToList());
        Guid id1 = first[0].Region.Id;
        Guid id2 = first[1].Region.Id;

        line2.Add(VBar(80, 300, 20));
        IReadOnlyList<RegionRecognition> second =
            recognizer.RecognizeRegions(line1.Concat(line2).ToList(), first);

        Assert.Equal(id1, second[0].Region.Id);
        Assert.Equal(id2, second[1].Region.Id);
    }

    [Fact]
    public void ReEditingTheSameLineTwice_KeepsTheUntouchedLineCleanBothTimes()
    {
        var line1 = new List<Stroke> { VBar(0, 0, 20), VBar(40, 0, 20) };
        var line2 = new List<Stroke> { VBar(0, 300, 20) };
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());

        IReadOnlyList<RegionRecognition> pass = recognizer.RecognizeRegions(line1.Concat(line2).ToList());
        RecognitionResult line1Result = pass[0].Result;

        for (int i = 1; i <= 2; i++)
        {
            line2.Add(VBar(40 * i, 300, 20));
            pass = recognizer.RecognizeRegions(line1.Concat(line2).ToList(), pass);
            Assert.False(pass[0].Dirty);
            Assert.Same(line1Result, pass[0].Result);   // line 1 keeps its very first read
        }
    }

    [Fact]
    public void EmptyPage_ReturnsNoRegions()
    {
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());
        Assert.Empty(recognizer.RecognizeRegions(Array.Empty<Stroke>()));
    }

    [Fact]
    public void InsertLineAbove_ExistingLineStaysClean_AndReusesItsResult()
    {
        // Region order changes (the new line lands at index 0) but the untouched line must keep its
        // id, stay clean, and hand back the SAME result object — the scenario id matching exists for.
        var bottom = new[] { VBar(0, 300, 20), VBar(40, 300, 20) };
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(bottom);
        RegionRecognition bottomFirst = Assert.Single(first);

        var top = new[] { VBar(0, 0, 20), VBar(40, 0, 20) };
        IReadOnlyList<RegionRecognition> second =
            recognizer.RecognizeRegions(top.Concat(bottom).ToList(), first);

        Assert.Equal(2, second.Count);
        Assert.True(second[0].Dirty);                                  // the new top line
        Assert.False(second[1].Dirty);                                 // the untouched bottom line
        Assert.Equal(bottomFirst.Region.Id, second[1].Region.Id);
        Assert.Same(bottomFirst.Result, second[1].Result);
    }

    // ---- deletion (the canvas edits increment 2 wires up) --------------------------------------------

    [Fact]
    public void ErasingOneStroke_RegionKeepsItsId_ButIsDirtyAndReRead()
    {
        var line = new List<Stroke> { VBar(0, 0, 20), VBar(40, 0, 20), VBar(80, 0, 20) };
        var classifier = new RecordingClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(line);
        RegionRecognition before = Assert.Single(first);
        int classifiedAfterFirst = classifier.SymbolsClassified;

        line.RemoveAt(2);
        IReadOnlyList<RegionRecognition> second = recognizer.RecognizeRegions(line, first);

        RegionRecognition after = Assert.Single(second);
        Assert.Equal(before.Region.Id, after.Region.Id);   // 2-of-3 plurality recovers the id
        Assert.True(after.Dirty);                          // ...but the stroke set changed
        Assert.NotSame(before.Result, after.Result);       // no stale-result resurrection
        Assert.Equal(classifiedAfterFirst + 2, classifier.SymbolsClassified);
    }

    [Fact]
    public void ErasingAWholeLine_DropsItsCachedResult_SurvivorStaysClean()
    {
        var line1 = new[] { VBar(0, 0, 20), VBar(40, 0, 20) };
        var line2 = new[] { VBar(0, 300, 20), VBar(40, 300, 20) };
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(line1.Concat(line2).ToList());
        RegionRecognition line1First = first[0];

        IReadOnlyList<RegionRecognition> second = recognizer.RecognizeRegions(line1, first);

        RegionRecognition survivor = Assert.Single(second);   // the erased line's entry is gone
        Assert.Equal(line1First.Region.Id, survivor.Region.Id);
        Assert.False(survivor.Dirty);
        Assert.Same(line1First.Result, survivor.Result);
    }

    [Fact]
    public void ErasingEverything_WithACachedPrevious_ReturnsEmptyWithoutClassifying()
    {
        var strokes = new[] { VBar(0, 0, 20), VBar(40, 0, 20) };
        var classifier = new RecordingClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);

        IReadOnlyList<RegionRecognition> first = recognizer.RecognizeRegions(strokes);
        int classifiedAfterFirst = classifier.SymbolsClassified;

        IReadOnlyList<RegionRecognition> second =
            recognizer.RecognizeRegions(Array.Empty<Stroke>(), first);

        Assert.Empty(second);
        Assert.Equal(classifiedAfterFirst, classifier.SymbolsClassified);
    }

    // ---- cancellation (the App's pen-input hot path in increment 2) ----------------------------------

    [Fact]
    public void RecognizeRegions_PreCancelledToken_ThrowsBeforeClassifyingAnything()
    {
        var classifier = new RecordingClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            recognizer.RecognizeRegions(new[] { VBar(0, 0, 20) }, null, cts.Token));
        Assert.Equal(0, classifier.SymbolsClassified);
    }

    [Fact]
    public void RecognizeRegion_PreCancelledToken_ThrowsBeforeClassifyingAnything()
    {
        var classifier = new RecordingClassifier();
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), classifier);
        InkSegmentation seg = new RegionSegmenter(new OverlapStrokeSegmenter())
            .Segment(new[] { VBar(0, 0, 20) });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            recognizer.RecognizeRegion(Assert.Single(seg.Regions), cts.Token));
        Assert.Equal(0, classifier.SymbolsClassified);
    }

    [Fact]
    public void RecognizeRegion_EmptyRegion_ReturnsAnEmptyResult()
    {
        // InkRegion is public — a caller can hand us an empty one; the contract is an empty read,
        // not a NaN confidence or a median-of-nothing crash.
        var recognizer = new ExpressionRecognizer(new OverlapStrokeSegmenter(), ByXBucket());
        var empty = new InkRegion(
            Guid.NewGuid(), Array.Empty<Guid>(), new InkBounds(0, 0, 0, 0), Array.Empty<StrokeGroup>());

        RecognitionResult result = recognizer.RecognizeRegion(empty);

        Assert.Equal(string.Empty, result.Latex);
        Assert.Empty(result.Tokens);
        Assert.Equal(0.0, result.Confidence);
    }

    // Labels a symbol by which x-bucket its box centre falls in — deterministic, position-only.
    private static FakeClassifier ByXBucket() => new(box =>
    {
        double cx = box.X + box.Width / 2.0;
        return cx < 320 ? "5" : cx < 400 ? "-" : cx < 470 ? "1" : "=";
    });

    private sealed class FakeClassifier : ISymbolClassifier
    {
        private readonly Func<InkBounds, string> _label;
        public FakeClassifier(Func<InkBounds, string> label) => _label = label;

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) =>
            new(_label(SymbolPreprocessor.Bounds(strokes)), 1.0);
    }

    // Records the context each symbol was judged against and counts how many symbols it classified —
    // enough to prove per-region context and that clean regions are never reclassified.
    private sealed class RecordingClassifier : ISymbolClassifier
    {
        public List<SymbolContext> Contexts { get; } = new();
        public int SymbolsClassified { get; private set; }

        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            Contexts.Add(context);
            SymbolsClassified++;
            return new SymbolPrediction("x", 1.0);
        }
    }

    private static Stroke VBar(double x, double y0, double height) =>
        new(Guid.NewGuid(), Enumerable.Range(0, 11)
            .Select(i => new StrokeSample(x, y0 + height * i / 10.0, TimeSpan.Zero, 0.5))
            .ToList());

    private static Stroke Line(double x1, double y1, double x2, double y2)
    {
        const int n = 12;
        var samples = new List<StrokeSample>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            samples.Add(new StrokeSample(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t, TimeSpan.Zero, 0.5));
        }
        return new Stroke(Guid.NewGuid(), samples);
    }
}
