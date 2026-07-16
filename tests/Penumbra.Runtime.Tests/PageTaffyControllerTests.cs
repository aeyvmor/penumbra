using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Runtime;
using Penumbra.Sheet;

namespace Penumbra.Runtime.Tests;

public sealed class PageTaffyControllerTests
{
    [Fact]
    public async Task Update_PublishesLiteralAndDependentGhostsWithoutMutatingCommittedState()
    {
        RegionRecognition definition = Region("x=5", 0, "x", "=", "5");
        RegionRecognition query = Region("x+1=", 80, "x", "+", "1", "=");
        (PageRecognitionSession page, InkDocument document) = await RecognizedPage(definition, query);
        var metrics = new BoundedInMemoryMetricsSink(16);
        var controller = new PageTaffyController(
            page,
            document,
            new HandwritingSynthesizer([new AnyGlyphSource()]),
            metrics: metrics);
        LiteralRun run = LiteralRuns.Find(definition.Result.Tokens).Single();
        Stroke[] documentBefore = document.Strokes.ToArray();
        EvaluationResult? definitionBefore = page.FindNode(definition.Region.Id)!.Result;
        EvaluationResult? queryBefore = page.FindNode(query.Region.Id)!.Result;

        Assert.True(controller.Begin(definition.Region.Id, run, new HashSet<Guid> { query.Region.Id }));
        PageTaffyUpdateResult update = controller.Update(28);

        Assert.True(update.Probed);
        Assert.Equal("x=7", update.TrialLatex);
        Assert.Equal("7", update.Frame!.Ghosts.Single(ghost => ghost.IsLiteral).ValueText);
        Assert.Equal(
            "8",
            update.Frame.Ghosts.Single(ghost => !ghost.IsLiteral && ghost.OwnerId == query.Region.Id).ValueText);
        Assert.True(run.SourceStrokeIds.All(update.Frame.MutedStrokeIds.Contains));
        Assert.Contains(query.Region.Id, update.Frame.HiddenAnswerOwnerIds);
        Assert.Equal(documentBefore, document.Strokes);
        Assert.False(document.CanUndo);
        Assert.Equal(definitionBefore, page.FindNode(definition.Region.Id)!.Result);
        Assert.Equal(queryBefore, page.FindNode(query.Region.Id)!.Result);
        Assert.Equal("x=5", page.PreviousRegions.Single(region => region.Region.Id == definition.Region.Id).Result.Latex);

        MetricObservation[] synthesis = metrics.Snapshot().Observations
            .Where(observation => observation.Operation == MetricOperation.TaffyGhostSynthesis)
            .ToArray();
        MetricObservation[] publication = metrics.Snapshot().Observations
            .Where(observation => observation.Operation == MetricOperation.TaffyPublication)
            .ToArray();
        Assert.Equal(new int?[] { 1, 1, 1 }, synthesis.Select(observation => observation.ItemCount));
        Assert.All(synthesis, observation => Assert.Equal(MetricOutcome.Completed, observation.Outcome));
        Assert.Equal(new int?[] { 1, 2 }, publication.Select(observation => observation.ItemCount));
        Assert.All(publication, observation => Assert.Equal(MetricOutcome.Completed, observation.Outcome));
        MetricObservation processing = Assert.Single(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.TaffyProcessing);
        Assert.Equal(MetricOutcome.Completed, processing.Outcome);
        Assert.Equal(2, processing.ItemCount);
    }

    [Fact]
    public async Task BeginAt_UsesCanvasToleranceAndRefusesWhenAnAnswerWinsTheHit()
    {
        RegionRecognition definition = Region("x=5", 0, "x", "=", "5");
        (PageRecognitionSession page, InkDocument document) = await RecognizedPage(definition);
        var controller = new PageTaffyController(
            page,
            document,
            new HandwritingSynthesizer([new AnyGlyphSource()]));
        LiteralRun run = LiteralRuns.Find(definition.Result.Tokens).Single();
        double y = run.UnionBounds.Y + run.UnionBounds.Height / 2;
        double paddedX = run.UnionBounds.X + run.UnionBounds.Width + 10;

        Assert.False(controller.BeginAt(paddedX, y, double.Epsilon, new HashSet<Guid>()));
        Assert.False(controller.BeginAt(
            paddedX,
            y,
            PageTaffyController.MinimumCanvasScale / 2,
            new HashSet<Guid>()));
        Assert.False(controller.BeginAt(
            paddedX,
            y,
            PageTaffyController.MaximumCanvasScale * 2,
            new HashSet<Guid>()));
        Assert.False(controller.BeginAt(paddedX, y, canvasScale: 2, new HashSet<Guid>()));
        Assert.True(controller.BeginAt(paddedX, y, canvasScale: 1, new HashSet<Guid>()));
        controller.End();

        Stroke overlapStroke = Stroke(Guid.NewGuid(), paddedX - 1, y, paddedX + 1, y);
        var overlapping = new SynthesizedHandwriting(
            [overlapStroke],
            new StrokeTimeline([overlapStroke]),
            []);
        Assert.False(controller.BeginAt(
            paddedX,
            y,
            canvasScale: 1,
            new HashSet<Guid>(),
            [overlapping]));
    }

    [Fact]
    public async Task BeginAt_GrabsAdjacentFractionNumeratorAndDenominatorIndependently()
    {
        RegionRecognition fraction = FractionQueryRegion();
        (PageRecognitionSession page, InkDocument document) = await RecognizedPage(fraction);
        var controller = new PageTaffyController(page, document, synthesizer: null);
        FractionNode fractionRoot = Assert.IsType<FractionNode>(
            Assert.IsType<RelationNode>(fraction.Result.ParseOutcome!.Root).Left);
        LeafNode numerator = Assert.IsType<LeafNode>(fractionRoot.Numerator);
        LeafNode denominator = Assert.IsType<LeafNode>(fractionRoot.Denominator);
        Assert.Equal("12", Assert.Single(LiteralRuns.Find(fraction.Result.Tokens)).ValueText);

        Assert.True(controller.BeginAt(
            numerator.Token.Bounds.X + 2,
            numerator.Token.Bounds.Y + 2,
            canvasScale: 1,
            new HashSet<Guid>()));
        Assert.Equal(@"\frac{2}{2}=", controller.Update(14).TrialLatex);
        controller.End();

        Assert.True(controller.BeginAt(
            denominator.Token.Bounds.X + 2,
            denominator.Token.Bounds.Y + 2,
            canvasScale: 1,
            new HashSet<Guid>()));
        Assert.Equal(@"\frac{1}{3}=", controller.Update(14).TrialLatex);
    }

    [Fact]
    public async Task Update_UsesCumulativeMotionRateFloorAndCachedRepeatedGeometry()
    {
        var time = new FakeTimeProvider();
        RegionRecognition definition = Region("x=5", 0, "x", "=", "5");
        (PageRecognitionSession page, InkDocument document) = await RecognizedPage(definition);
        var controller = new PageTaffyController(
            page,
            document,
            new HandwritingSynthesizer([new AnyGlyphSource()]),
            time);
        LiteralRun run = LiteralRuns.Find(definition.Result.Tokens).Single();
        Assert.True(controller.Begin(definition.Region.Id, run, new HashSet<Guid>()));

        Assert.Equal(PageTaffyRefusal.InvalidLiteral, controller.Update(double.NaN).Refusal);
        Assert.Equal(PageTaffyRefusal.InvalidLiteral, controller.Update(double.PositiveInfinity).Refusal);
        Assert.Equal(0, controller.ProbeCount);
        Assert.Equal(PageTaffyRefusal.UnchangedValue, controller.Update(13).Refusal);
        PageTaffyUpdateResult seven = controller.Update(28);
        SynthesizedHandwriting firstSeven = seven.Frame!.Ghosts.Single(ghost => ghost.IsLiteral).Handwriting;
        Assert.Equal(PageTaffyRefusal.RateLimited, controller.Update(14).Refusal);

        time.Advance(PageTaffyController.ProbeFloor + TimeSpan.FromMilliseconds(1));
        Assert.Equal("x=6", controller.Update(14).TrialLatex);
        time.Advance(PageTaffyController.ProbeFloor + TimeSpan.FromMilliseconds(1));
        PageTaffyUpdateResult repeated = controller.Update(28);

        Assert.Equal(3, controller.ProbeCount);
        Assert.Same(
            firstSeven,
            repeated.Frame!.Ghosts.Single(ghost => ghost.IsLiteral).Handwriting);
    }

    [Fact]
    public async Task Update_FallsBackToFlatSpliceWhenTheRegionCarriesNoStructuralOpinion()
    {
        // A pre-5.5/legacy result (ParseOutcome null, e.g. a stale v1-v3 cache or a non-conforming
        // recognizer) must keep working through the original flat LiteralRuns.Splice path.
        RegionRecognition definition = FlatRegion("x=5", 0, "x", "=", "5");
        (PageRecognitionSession page, InkDocument document) = await RecognizedPage(definition);
        Assert.Null(definition.Result.ParseOutcome);
        var controller = new PageTaffyController(
            page,
            document,
            new HandwritingSynthesizer([new AnyGlyphSource()]));
        LiteralRun run = LiteralRuns.Find(definition.Result.Tokens).Single();

        Assert.True(controller.Begin(definition.Region.Id, run, new HashSet<Guid>()));
        PageTaffyUpdateResult update = controller.Update(28);

        Assert.True(update.Probed);
        Assert.Equal("x=7", update.TrialLatex);
    }

    [Fact]
    public async Task StructurallyRefusedRegion_NeverReachesAcceptedRegionsSoTaffyCannotGrabIntoIt()
    {
        // End-to-end proof of the structural-refusal contract: RecognitionGate already keeps a
        // non-accepted ParseOutcome out of AcceptedRegions (confidence/OOD is checked first, but a
        // structural refusal independently fails the same gate — see RecognitionGate.cs), so the region
        // never becomes grabbable through Begin/BeginAt. TaffyLiteralTreeTests pins the defense-in-depth
        // guard PageTaffyController.Begin now also carries directly, since that branch cannot be reached
        // through this public session surface.
        RegionRecognition refused = StructurallyRefusedRegion(0, "(", "x", "+", "1");
        (PageRecognitionSession page, InkDocument document) = await RecognizedPage(refused);
        Assert.Empty(page.AcceptedRegions);
        var controller = new PageTaffyController(
            page,
            document,
            new HandwritingSynthesizer([new AnyGlyphSource()]));
        LiteralRun run = LiteralRuns.Find(refused.Result.Tokens).Single();

        Assert.False(controller.Begin(refused.Region.Id, run, new HashSet<Guid>()));
        Assert.False(controller.BeginAt(
            run.UnionBounds.X + run.UnionBounds.Width / 2,
            run.UnionBounds.Y + run.UnionBounds.Height / 2,
            canvasScale: 1,
            new HashSet<Guid>()));
    }

    private static async Task<(PageRecognitionSession Page, InkDocument Document)> RecognizedPage(
        params RegionRecognition[] regions)
    {
        Stroke[] strokes = regions
            .SelectMany(region => region.Region.Groups)
            .SelectMany(group => group.Strokes)
            .ToArray();
        var document = new InkDocument();
        document.Load(PenumbraDocumentSerializer.CreateEmpty() with
        {
            Strokes = strokes,
            StrokeMetadata = strokes.Select(stroke => new PersistedStrokeMetadata(
                stroke.Id,
                StrokeOriginNames.UserInk)).ToArray(),
        });
        var page = new PageRecognitionSession(
            new FixedRecognizer(regions),
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            RecognitionCalibration.Default.MinConfidence);
        page.ApplyAndCommit(await page.RecognizeAsync(document.Strokes));
        return (page, document);
    }

    /// <summary>
    /// Builds an accepted region by running the SAME real <see cref="SpatialLayoutParser"/> the product
    /// pipeline uses, so <c>Result.ParseOutcome</c> carries a genuine accepted layout tree (not a hand-typed
    /// stand-in). This exercises <see cref="PageTaffyController"/>'s tree-aware path in every test that uses
    /// this helper — the unchanged assertions across the file are the byte-parity proof: the same trial LaTeX,
    /// ghost text, and cached handwriting identity the old flat-splice path produced.
    /// </summary>
    private static RegionRecognition Region(string latex, double y, params string[] labels)
    {
        (Stroke[] strokes, RecognizedToken[] tokens) = BuildTokens(y, labels);
        SymbolPrediction[] predictions = tokens
            .Select(token => new SymbolPrediction(token.Latex, token.Confidence))
            .ToArray();
        SpatialParseResult parse = SpatialLayoutParser.Parse(tokens, predictions);
        if (!parse.Outcome.IsAccepted)
        {
            throw new InvalidOperationException(
                $"Test fixture expected an accepted parse for '{latex}' but the real spatial parser refused "
                + $"({parse.Outcome.Reason}: {parse.Outcome.Detail}).");
        }

        string realLatex = LayoutLatexSerializer.Serialize(parse.Outcome.Root!);
        if (realLatex != latex)
        {
            throw new InvalidOperationException(
                $"Test fixture drift: expected '{latex}' but the real spatial parser produced '{realLatex}'.");
        }

        return BuildRegion(y, strokes, parse.Tokens, realLatex, parse.Outcome);
    }

    private static RegionRecognition FractionQueryRegion()
    {
        Guid numeratorId = Guid.NewGuid();
        Guid denominatorId = Guid.NewGuid();
        Guid barId = Guid.NewGuid();
        Guid equalsId = Guid.NewGuid();
        Stroke[] strokes =
        [
            Stroke(numeratorId, 10, 10, 20, 20),
            Stroke(denominatorId, 10, 30, 20, 40),
            Stroke(barId, 8, 25, 22, 25),
            Stroke(equalsId, 40, 20, 50, 30),
        ];
        var numerator = new RecognizedToken("1", [numeratorId], new InkBounds(10, 10, 10, 10), 0.99);
        var denominator = new RecognizedToken("2", [denominatorId], new InkBounds(10, 30, 10, 10), 0.99);
        var bar = new RecognizedToken("-", [barId], new InkBounds(8, 25, 14, 1), 0.99);
        var equals = new RecognizedToken("=", [equalsId], new InkBounds(40, 20, 10, 10), 0.99);
        RecognizedToken[] tokens = [numerator, denominator, bar, equals];
        LayoutNode tree = new RelationNode(
            new FractionNode(new LeafNode(numerator), new LeafNode(denominator), bar),
            equals,
            right: null);
        return BuildRegion(
            0,
            strokes,
            tokens,
            @"\frac{1}{2}=",
            LayoutParseOutcome.Accepted(tree));
    }

    /// <summary>
    /// Builds an accepted region with NO structural opinion (<c>ParseOutcome</c> null) — the pre-5.5/legacy
    /// shape a persisted v1-v3 cache or a non-conforming recognizer can still produce. Taffy must keep
    /// falling back to flat <see cref="LiteralRuns.Splice"/> for these.
    /// </summary>
    private static RegionRecognition FlatRegion(string latex, double y, params string[] labels)
    {
        (Stroke[] strokes, RecognizedToken[] tokens) = BuildTokens(y, labels);
        return BuildRegion(y, strokes, tokens, latex, parseOutcome: null);
    }

    /// <summary>
    /// Builds a region whose real spatial parser verdict is a structural refusal (unmatched bracket) —
    /// used to prove <see cref="TaffyLiteralTree"/>'s structural-refusal guard directly, since
    /// <see cref="RecognitionGate"/> already keeps such a region out of a real session's
    /// <c>AcceptedRegions</c> (see <see cref="TaffyLiteralTree"/>'s remarks).
    /// </summary>
    private static RegionRecognition StructurallyRefusedRegion(double y, params string[] labels)
    {
        (Stroke[] strokes, RecognizedToken[] tokens) = BuildTokens(y, labels);
        SymbolPrediction[] predictions = tokens
            .Select(token => new SymbolPrediction(token.Latex, token.Confidence))
            .ToArray();
        SpatialParseResult parse = SpatialLayoutParser.Parse(tokens, predictions);
        if (parse.Outcome.IsAccepted)
        {
            throw new InvalidOperationException("Test fixture expected a structural refusal but was accepted.");
        }

        string flatLatex = TokenLatexAssembler.Assemble(parse.Tokens.Select(token => token.Latex).ToList());
        return BuildRegion(y, strokes, parse.Tokens, flatLatex, parse.Outcome);
    }

    private static (Stroke[] Strokes, RecognizedToken[] Tokens) BuildTokens(double y, string[] labels)
    {
        var strokes = new List<Stroke>(labels.Length);
        var tokens = new List<RecognizedToken>(labels.Length);
        for (int index = 0; index < labels.Length; index++)
        {
            Guid id = Guid.NewGuid();
            double x = 10 + index * 28;
            Stroke stroke = Stroke(id, x, y + 10, x + 16, y + 40);
            var bounds = new InkBounds(x, y + 10, 16, 30);
            strokes.Add(stroke);
            tokens.Add(new RecognizedToken(labels[index], [id], bounds, 0.99));
        }

        return (strokes.ToArray(), tokens.ToArray());
    }

    private static RegionRecognition BuildRegion(
        double y,
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<RecognizedToken> tokens,
        string latex,
        LayoutParseOutcome? parseOutcome)
    {
        var regionBounds = new InkBounds(10, y + 10, Math.Max(16, tokens.Count * 28 - 12), 30);
        return new RegionRecognition(
            new InkRegion(
                Guid.NewGuid(),
                strokes.Select(stroke => stroke.Id).ToArray(),
                regionBounds,
                strokes.Zip(tokens, (stroke, token) => new StrokeGroup([stroke], token.Bounds)).ToArray()),
            new RecognitionResult(latex, tokens, 0.99, 0.99, parseOutcome),
            Dirty: true);
    }

    private static Stroke Stroke(Guid id, double x1, double y1, double x2, double y2) => new(id,
    [
        new StrokeSample(x1, y1, TimeSpan.Zero, 0.5),
        new StrokeSample(x2, y2, TimeSpan.FromMilliseconds(20), 0.5),
    ]);

    private sealed class FixedRecognizer(IReadOnlyList<RegionRecognition> regions) : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => regions;

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Task.FromResult(regions);
    }

    private sealed class AnyGlyphSource : IGlyphSource
    {
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) =>
        [
            Stroke(Guid.NewGuid(), 0, 0, 1, 1),
        ];
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (_now - _origin).Ticks;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
