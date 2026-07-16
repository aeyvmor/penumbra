using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Graphing;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.App.Tests;

/// <summary>
/// Headless Phase 6 coverage: the graph panel's detection wiring over accepted page state, default-domain
/// sampling, co-plotting, honest absence for ordinary math, clear-page behaviour, pan/zoom re-sampling
/// through the <see cref="IDomainSampler"/> seam, and the ROADMAP parity item (a recognized layout tree and
/// the same expression as a typed string produce identical sampled series). ScottPlot rendering itself is
/// deliberately not tested here — the View stays thin and is dogfooded visually.
/// </summary>
public sealed class GraphPanelViewModelTests
{
    // ---------- GraphPanelViewModel unit tests (real detector + sampler unless stated) ----------

    [Fact]
    public void AcceptedGraphLine_ProducesOneNamedCurveOverTheDefaultDomain()
    {
        GraphPanelViewModel panel = CreatePanel();

        panel.UpdateFromAcceptedRegions(new[] { Region("y=x^2", y: 0) });

        GraphCurveModel curve = Assert.Single(panel.Curves);
        Assert.True(panel.HasCurves);
        Assert.Equal("y = x^2", curve.Name);
        Assert.Equal("y", curve.DependentVariable);
        Assert.Equal("x", curve.IndependentVariable);
        Assert.Equal(0, curve.ColorIndex);
        GraphSegment segment = Assert.Single(curve.Series.Segments);
        Assert.Equal(GraphPanelViewModel.DefaultSampleCount, segment.Points.Count);
        Assert.Equal(GraphPanelViewModel.DefaultDomainMin, segment.Points[0].X);
        Assert.Equal(GraphPanelViewModel.DefaultDomainMax, segment.Points[^1].X);
    }

    [Fact]
    public void NonYVariableNames_AreHonoredInNameAndAxes()
    {
        GraphPanelViewModel panel = CreatePanel();

        panel.UpdateFromAcceptedRegions(new[] { Region("f=t^2", y: 0) });

        GraphCurveModel curve = Assert.Single(panel.Curves);
        Assert.Equal("f = t^2", curve.Name);
        Assert.Equal("f", curve.DependentVariable);
        Assert.Equal("t", curve.IndependentVariable);
    }

    [Fact]
    public void MultipleCandidates_CoPlotWithDistinctColors()
    {
        GraphPanelViewModel panel = CreatePanel();

        panel.UpdateFromAcceptedRegions(new[]
        {
            Region("y=x^2", y: 0),
            Region(@"y=\sin(x)", y: 80),
        });

        Assert.Equal(2, panel.Curves.Count);
        Assert.Equal(new[] { "y = x^2", @"y = \sin(x)" }, panel.Curves.Select(curve => curve.Name).ToArray());
        Assert.Equal(new[] { 0, 1 }, panel.Curves.Select(curve => curve.ColorIndex).ToArray());
    }

    [Fact]
    public void SurvivingCurve_KeepsItsColorWhenAnotherLineDisappears()
    {
        GraphPanelViewModel panel = CreatePanel();
        RegionRecognition first = Region("y=x^2", y: 0);
        RegionRecognition second = Region(@"y=\sin(x)", y: 80);
        panel.UpdateFromAcceptedRegions(new[] { first, second });

        panel.UpdateFromAcceptedRegions(new[] { second });

        GraphCurveModel survivor = Assert.Single(panel.Curves);
        Assert.Equal(@"y = \sin(x)", survivor.Name);
        Assert.Equal(1, survivor.ColorIndex);
    }

    [Theory]
    [InlineData("2+3=")] // trailing-relation query: a compute request, not a curve
    [InlineData("x=5")] // constant definition: Sheet state, not a curve
    [InlineData("2x=6")] // equation to solve: LHS is not a bare variable
    [InlineData("x+1")] // plain statement: no relation at all
    public void OrdinaryNonGraphMath_ProducesNoCurveAndNoError(string latex)
    {
        GraphPanelViewModel panel = CreatePanel();

        panel.UpdateFromAcceptedRegions(new[] { Region(latex, y: 0) });

        Assert.Empty(panel.Curves);
        Assert.False(panel.HasCurves);
    }

    [Fact]
    public void MixedPage_PlotsOnlyTheGraphableLines()
    {
        GraphPanelViewModel panel = CreatePanel();

        panel.UpdateFromAcceptedRegions(new[]
        {
            Region("x=5", y: 0),
            Region("y=x^2", y: 80),
            Region("2+3=", y: 160),
        });

        GraphCurveModel curve = Assert.Single(panel.Curves);
        Assert.Equal("y = x^2", curve.Name);
    }

    [Fact]
    public void SetDomain_ResamplesEveryCurveThroughTheSamplerWithoutRedetection()
    {
        var detector = new CountingGraphDetector();
        var sampler = new RecordingDomainSampler();
        var panel = new GraphPanelViewModel(detector, sampler);
        panel.UpdateFromAcceptedRegions(new[] { Region("y=x", y: 0) });
        Assert.Equal(1, detector.DetectCount);
        (GraphCandidate candidate, GraphDomain domain, int count) = Assert.Single(sampler.Calls);
        Assert.Equal(GraphPanelViewModel.DefaultDomainMin, domain.Min);
        Assert.Equal(GraphPanelViewModel.DefaultDomainMax, domain.Max);
        Assert.Equal(GraphPanelViewModel.DefaultSampleCount, count);

        panel.SetDomain(-2, 5);

        Assert.Equal(1, detector.DetectCount); // pan/zoom re-samples; it never re-detects
        Assert.Equal(2, sampler.Calls.Count);
        (GraphCandidate resampledCandidate, GraphDomain resampledDomain, int resampledCount) = sampler.Calls[1];
        Assert.Same(candidate, resampledCandidate);
        Assert.Equal(-2, resampledDomain.Min);
        Assert.Equal(5, resampledDomain.Max);
        Assert.Equal(GraphPanelViewModel.DefaultSampleCount, resampledCount);
        Assert.Equal(-2, panel.Domain.Min);
        Assert.Equal(5, panel.Domain.Max);
    }

    [Fact]
    public void GapSegmentedSeries_PassesThroughToTheCurveModelUnmerged()
    {
        var sampler = new RecordingDomainSampler(SeriesWithSegments(2));
        var panel = new GraphPanelViewModel(new GraphDetector(), sampler);

        panel.UpdateFromAcceptedRegions(new[] { Region("y=x", y: 0) });

        GraphCurveModel curve = Assert.Single(panel.Curves);
        Assert.Equal(2, curve.Series.Segments.Count);
    }

    [Fact]
    public void SamplingRefusal_OmitsTheCurveInsteadOfThrowing()
    {
        var sampler = new RefusingDomainSampler();
        var panel = new GraphPanelViewModel(new GraphDetector(), sampler);

        panel.UpdateFromAcceptedRegions(new[] { Region("y=x", y: 0) });

        Assert.Empty(panel.Curves);
    }

    [Fact]
    public void Clear_DropsCurvesAndRestartsColorAssignment()
    {
        GraphPanelViewModel panel = CreatePanel();
        panel.UpdateFromAcceptedRegions(new[] { Region("y=x^2", y: 0), Region(@"y=\sin(x)", y: 80) });

        panel.Clear();
        Assert.Empty(panel.Curves);
        Assert.False(panel.HasCurves);

        panel.UpdateFromAcceptedRegions(new[] { Region(@"y=\sin(x)", y: 80) });
        Assert.Equal(0, Assert.Single(panel.Curves).ColorIndex);
    }

    // ---------- MainWindowViewModel wiring (the panel follows the accepted page transaction) ----------

    [Fact]
    public async Task RecognizedGraphLine_AppearsInThePanelWhileOrdinaryMathDoesNot()
    {
        var graphLine = Region("y=x^2", y: 0);
        var query = Region("2+3=", y: 80);
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { graphLine, query }));
        AddInk(vm, graphLine, query);

        await vm.RecognizeNowAsync();

        GraphCurveModel curve = Assert.Single(vm.GraphPanel.Curves);
        Assert.Equal("y = x^2", curve.Name);
        Assert.Equal(graphLine.Region.Id, curve.OwnerId);
    }

    [Fact]
    public async Task RejectedLine_NeverReachesTheGraphPanel()
    {
        RegionRecognition shaky = Region("y=x^2", y: 0);
        RecognizedToken uncertain = shaky.Result.Tokens[0] with { Confidence = .1 };
        shaky = shaky with
        {
            Result = shaky.Result with { Tokens = new[] { uncertain }, MinConfidence = .1 },
        };
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { shaky }));
        AddInk(vm, shaky);

        await vm.RecognizeNowAsync();

        Assert.Empty(vm.GraphPanel.Curves);
    }

    [Fact]
    public async Task ClearingThePage_ClearsTheCurves()
    {
        var graphLine = Region("y=x^2", y: 0);
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { graphLine }));
        AddInk(vm, graphLine);
        await vm.RecognizeNowAsync();
        Assert.Single(vm.GraphPanel.Curves);

        vm.Document.Clear();

        Assert.Empty(vm.GraphPanel.Curves);
        Assert.False(vm.GraphPanel.HasCurves);
    }

    [Fact]
    public async Task ErasedGraphLine_DropsItsCurveOnTheNextPass()
    {
        var graphLine = Region("y=x^2", y: 0);
        using MainWindowViewModel vm = Create(
            new QueueRecognizer(new[] { graphLine }, Array.Empty<RegionRecognition>()));
        AddInk(vm, graphLine);
        await vm.RecognizeNowAsync();
        Assert.Single(vm.GraphPanel.Curves);

        vm.Document.EraseStrokes(graphLine.Region.StrokeIds);
        await vm.RecognizeNowAsync();

        Assert.Empty(vm.GraphPanel.Curves);
    }

    [Fact]
    public void RegionWithAcceptedLayoutTree_DetectsThroughTheTreeEntryPoint()
    {
        var spy = new OverloadSpyDetector();
        var panel = new GraphPanelViewModel(spy, new DomainSampler());
        LayoutNode tree = Eq(Leaf("y"), Sup(Leaf("x"), Leaf("2")));
        RegionRecognition treeRegion = Region("y=x^2", y: 0);
        treeRegion = treeRegion with
        {
            Result = treeRegion.Result with { ParseOutcome = LayoutParseOutcome.Accepted(tree) },
        };

        panel.UpdateFromAcceptedRegions(new[] { treeRegion, Region("y=x", y: 80) });

        Assert.Equal(1, spy.TreeCalls); // the region carrying a layout root used the tree path
        Assert.Equal(1, spy.StringCalls); // the opinion-less region fell back to its LaTeX string
        Assert.Equal(2, panel.Curves.Count);
    }

    // ---------- ROADMAP parity: recognized tree and typed string produce identical series ----------

    public static IEnumerable<object[]> ParityCases()
    {
        yield return new object[] { Eq(Leaf("y"), Sup(Leaf("x"), Leaf("2"))), "y=x^2" };
        yield return new object[] { Eq(Leaf("y"), FunctionCall("sin", Leaf("x"))), @"y=\sin(x)" };
        yield return new object[] { Eq(Leaf("y"), Leaf("x")), "y=x" };
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public async Task RecognizedTreeAndTypedString_ProduceIdenticalSampledSeries(LayoutNode tree, string latex)
    {
        // The tree path: an accepted region whose RecognitionResult carries the spatial grammar's layout
        // root, exactly as Phase 5.5 recognition publishes it. Uses the REAL detector and sampler.
        RegionRecognition treeRegion = Region(latex, y: 0);
        treeRegion = treeRegion with
        {
            Result = treeRegion.Result with
            {
                Latex = LayoutLatexSerializer.Serialize(tree),
                ParseOutcome = LayoutParseOutcome.Accepted(tree),
            },
        };
        using MainWindowViewModel treeVm = Create(new QueueRecognizer(new[] { treeRegion }));
        AddInk(treeVm, treeRegion);
        await treeVm.RecognizeNowAsync();

        // The string path: the same expression as typed LaTeX with no structural opinion attached.
        RegionRecognition stringRegion = Region(latex, y: 0);
        using MainWindowViewModel stringVm = Create(new QueueRecognizer(new[] { stringRegion }));
        AddInk(stringVm, stringRegion);
        await stringVm.RecognizeNowAsync();

        GraphCurveModel fromTree = Assert.Single(treeVm.GraphPanel.Curves);
        GraphCurveModel fromString = Assert.Single(stringVm.GraphPanel.Curves);
        Assert.Equal(fromString.DependentVariable, fromTree.DependentVariable);
        Assert.Equal(fromString.IndependentVariable, fromTree.IndependentVariable);
        Assert.Equal(fromString.Series, fromTree.Series); // GraphSeries equality is structural, point-by-point
    }

    // ---------- helpers ----------

    private static GraphPanelViewModel CreatePanel() =>
        new(new GraphDetector(), new DomainSampler());

    private static MainWindowViewModel Create(IRegionRecognizer recognizer) => new(
        recognizer,
        new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
        glyphBank: null,
        synthesizer: null,
        calibration: RecognitionCalibration.Default,
        graphDetector: new GraphDetector(),
        domainSampler: new DomainSampler());

    private static RegionRecognition Region(string latex, double y)
    {
        Guid strokeId = Guid.NewGuid();
        var stroke = new Stroke(strokeId, new[]
        {
            new StrokeSample(10, y + 10, TimeSpan.Zero, .5),
            new StrokeSample(20, y + 30, TimeSpan.FromMilliseconds(20), .5),
        });
        var bounds = new InkBounds(10, y + 10, 20, 30);
        var token = new RecognizedToken("=", new[] { strokeId }, bounds, .99);
        var region = new InkRegion(
            Guid.NewGuid(), new[] { strokeId }, bounds, new[] { new StrokeGroup(new[] { stroke }, bounds) });
        return new RegionRecognition(region, new RecognitionResult(latex, new[] { token }, .99, .99), Dirty: true);
    }

    private static void AddInk(MainWindowViewModel vm, params RegionRecognition[] regions)
    {
        vm.LiveRecognition = false;
        foreach (Stroke stroke in regions.SelectMany(r => r.Region.Groups).SelectMany(group => group.Strokes))
        {
            if (vm.Document.Strokes.All(existing => existing.Id != stroke.Id))
            {
                vm.Document.AddStroke(stroke);
            }
        }
    }

    private static GraphSeries SeriesWithSegments(int segmentCount) => new(
        Enumerable.Range(0, segmentCount)
            .Select(i => new GraphSegment(new[] { new GraphPoint(i, i), new GraphPoint(i + .5, i) }))
            .ToArray());

    // Layout-tree builders mirroring Penumbra.Graphing.Tests.LayoutTreeFactory (internal there, so the
    // handful this parity fixture needs are reconstructed from the same public Core.Layout constructors).
    private static RecognizedToken Tok(string latex) =>
        new(latex, new[] { Guid.NewGuid() }, default, Confidence: 1.0);

    private static LeafNode Leaf(string latex) => new(Tok(latex));

    private static ScriptNode Sup(LayoutNode @base, LayoutNode superscript) => new(@base, superscript, null);

    private static RelationNode Eq(LayoutNode left, LayoutNode? right) => new(left, Tok("="), right);

    private static FunctionCallNode FunctionCall(string name, LayoutNode argument) =>
        new(name, name.Select(c => Tok(c.ToString())).ToArray(), argument);

    private sealed class QueueRecognizer(params IReadOnlyList<RegionRecognition>[] results) : IRegionRecognizer
    {
        private readonly Queue<IReadOnlyList<RegionRecognition>> _results = new(results);

        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Task.FromResult(_results.Dequeue());
    }

    /// <summary>Delegates to the real detector while counting calls, proving pan/zoom never re-detects.</summary>
    private sealed class CountingGraphDetector : IGraphDetector
    {
        private readonly GraphDetector _inner = new();

        public int DetectCount { get; private set; }

        public GraphDetectionOutcome Detect(LayoutNode root)
        {
            DetectCount++;
            return _inner.Detect(root);
        }

        public GraphDetectionOutcome Detect(string latex)
        {
            DetectCount++;
            return _inner.Detect(latex);
        }
    }

    private sealed class RecordingDomainSampler(GraphSeries? series = null) : IDomainSampler
    {
        private readonly GraphSeries _series = series ?? SeriesWithSegments(1);

        public List<(GraphCandidate Candidate, GraphDomain Domain, int SampleCount)> Calls { get; } = new();

        public GraphSamplingOutcome SampleSeries(GraphCandidate candidate, GraphDomain domain, int sampleCount)
        {
            Calls.Add((candidate, domain, sampleCount));
            return GraphSamplingOutcome.Sampled(_series);
        }
    }

    private sealed class RefusingDomainSampler : IDomainSampler
    {
        public GraphSamplingOutcome SampleSeries(GraphCandidate candidate, GraphDomain domain, int sampleCount) =>
            GraphSamplingOutcome.Refused(GraphSamplingRefusalReason.UncompilableExpression);
    }

    /// <summary>Counts which IGraphDetector entry point handled each region (tree versus string fallback).</summary>
    private sealed class OverloadSpyDetector : IGraphDetector
    {
        private readonly GraphDetector _inner = new();

        public int TreeCalls { get; private set; }

        public int StringCalls { get; private set; }

        public GraphDetectionOutcome Detect(LayoutNode root)
        {
            TreeCalls++;
            return _inner.Detect(root);
        }

        public GraphDetectionOutcome Detect(string latex)
        {
            StringCalls++;
            return _inner.Detect(latex);
        }
    }
}
