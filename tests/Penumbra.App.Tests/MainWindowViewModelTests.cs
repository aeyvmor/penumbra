using Microsoft.Extensions.DependencyInjection;
using Penumbra.App.Services;
using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task ChainBuildsGraphAndOnlyQueriesOwnAnswers()
    {
        var x = Region("x=5", y: 0);
        var y = Region("y=x+2", y: 80);
        var query = Region("y+1=", y: 160);
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { x, y, query }));
        AddInk(vm, x, y, query);

        await vm.RecognizeNowAsync();

        Assert.Equal(3, vm.SheetNodes.Count);
        Assert.Equal(NodeRole.Query, vm.SheetNodes.Single(node => node.Id == query.Region.Id).Role);
        Assert.True(vm.SheetNodes.Single(node => node.Id == query.Region.Id).Result?.IsComputed);
        Assert.Single(vm.AnswerLayer.Answers);
        Assert.Equal(query.Region.Id, vm.AnswerLayer.Answers[0].OwnerId);
    }

    [Fact]
    public async Task DirtySourceRecomputesEqualDependentsWithoutReplayingAnswer()
    {
        var x1 = Region("x=5", y: 0);
        var y1 = Region("y=x+2", y: 80);
        var q1 = Region("y+1=", y: 160);
        var x2 = Region("x=5.0", y: 0, id: x1.Region.Id, strokeId: x1.Region.StrokeIds[0]);
        var y2 = y1 with { Dirty = false };
        var q2 = q1 with { Dirty = false };
        var recognizer = new QueueRecognizer(new[] { x1, y1, q1 }, new[] { x2, y2, q2 });
        using MainWindowViewModel vm = Create(recognizer);
        AddInk(vm, x1, y1, q1);
        await vm.RecognizeNowAsync();
        long sequence = vm.AnswerLayer.Answers.Single().Sequence;

        await vm.RecognizeNowAsync();

        Assert.Equal(sequence, vm.AnswerLayer.Answers.Single().Sequence);
        Assert.NotNull(vm.CausalityRipple);
        Assert.Contains(vm.CausalityRipple!.Steps, step => step.OwnerId == q1.Region.Id);
        Assert.Empty(recognizer.PreviousArguments[0]!);
        Assert.Equal(3, recognizer.PreviousArguments[1]!.Count);
    }

    [Fact]
    public async Task RejectedOrDeletedRegionIsRemovedWithItsAnswer()
    {
        var q1 = Region("2+2=", y: 0);
        var q2 = Region("3+3=", y: 80);
        var rejectedToken = q1.Result.Tokens[0] with { Confidence = .1 };
        var rejected = q1 with
        {
            Result = q1.Result with { Tokens = new[] { rejectedToken }, MinConfidence = .1 },
            Dirty = true,
        };
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { q1, q2 }, new[] { rejected }));
        AddInk(vm, q1, q2);
        await vm.RecognizeNowAsync();
        Assert.Equal(2, vm.AnswerLayer.Answers.Count);

        await vm.RecognizeNowAsync();

        Assert.Empty(vm.AnswerLayer.Answers);
        Assert.Empty(vm.SheetNodes);
        Assert.NotEmpty(vm.UncertainStrokeIds);
    }

    [Fact]
    public async Task ChangedQueryReplacesOnlyItsOwnedAnswer()
    {
        var q1 = Region("2+2=", y: 0);
        var q2 = Region("3+3=", y: 80);
        var q1Changed = Region("2+3=", y: 0, id: q1.Region.Id, strokeId: q1.Region.StrokeIds[0]);
        var q2Clean = q2 with { Dirty = false };
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { q1, q2 }, new[] { q1Changed, q2Clean }));
        AddInk(vm, q1, q2);
        await vm.RecognizeNowAsync();
        Dictionary<Guid, long> before = vm.AnswerLayer.Answers.ToDictionary(answer => answer.OwnerId, answer => answer.Sequence);

        await vm.RecognizeNowAsync();

        Dictionary<Guid, long> after = vm.AnswerLayer.Answers.ToDictionary(answer => answer.OwnerId, answer => answer.Sequence);
        Assert.NotEqual(before[q1.Region.Id], after[q1.Region.Id]);
        Assert.Equal(before[q2.Region.Id], after[q2.Region.Id]);
    }

    [Fact]
    public async Task ErasedRegionDisappearsFromGraphAndUndoCanRestoreIt()
    {
        var query = Region("2+2=", y: 0);
        var restored = query with { Dirty = false };
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { query }, Array.Empty<RegionRecognition>(), new[] { restored }));
        AddInk(vm, query);
        await vm.RecognizeNowAsync();

        vm.Document.EraseStrokes(query.Region.StrokeIds);
        await vm.RecognizeNowAsync();
        Assert.Empty(vm.SheetNodes);
        Assert.Empty(vm.AnswerLayer.Answers);

        vm.Document.Undo();
        await vm.RecognizeNowAsync();
        Assert.Single(vm.SheetNodes);
        Assert.Single(vm.AnswerLayer.Answers);
    }

    [Fact]
    public async Task SupersededNonCooperativePassCannotBecomeGraphOrPreviousState()
    {
        var stale = Region("x=1", y: 0);
        var fresh = Region("x=2", y: 0, id: stale.Region.Id, strokeId: stale.Region.StrokeIds[0]);
        var recognizer = new ControlledRecognizer();
        using MainWindowViewModel vm = Create(recognizer);
        AddInk(vm, stale);

        Task first = vm.RecognizeNowAsync();
        await recognizer.WaitForCallsAsync(1);
        Task second = vm.RecognizeNowAsync();
        await recognizer.WaitForCallsAsync(2);
        recognizer.Complete(1, new[] { fresh });
        await second;
        recognizer.Complete(0, new[] { stale });
        await first;
        Task third = vm.RecognizeNowAsync();
        await recognizer.WaitForCallsAsync(3);

        Assert.Equal("x=2", vm.SheetNodes.Single().Latex);
        Assert.Equal("x=2", recognizer.PreviousArguments[2]!.Single().Result.Latex);
        recognizer.Complete(2, new[] { fresh with { Dirty = false } });
        await third;
    }

    [Fact]
    public async Task DocumentMutationInvalidatesNonCooperativeInFlightPassImmediately()
    {
        var stale = Region("x=1", y: 0);
        var recognizer = new ControlledRecognizer();
        using MainWindowViewModel vm = Create(recognizer);
        AddInk(vm, stale);

        Task read = vm.RecognizeNowAsync();
        await recognizer.WaitForCallsAsync(1);

        // Direct document edits (including undo/redo and eraser completion) do not necessarily pass
        // through the canvas's DrawingStarted event, but they still make the read snapshot obsolete.
        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(200, 200, TimeSpan.Zero, .5),
        }));
        recognizer.Complete(0, new[] { stale });
        await read;

        Assert.Empty(vm.SheetNodes);
        Assert.Empty(vm.AnswerLayer.Answers);
    }

    [Fact]
    public void QuietPeriod_ErasePausesLonger_AddsAndLoadsDoNot()
    {
        // s19 dogfood: erase → pause → rewrite used to recompute the half-edited line mid-pause.
        // Removing strokes earns the erase grace; adding or holding steady stays on the live period.
        Assert.Equal(MainWindowViewModel.EraseQuietPeriod, MainWindowViewModel.QuietPeriodFor(5, 4));
        Assert.Equal(MainWindowViewModel.EraseQuietPeriod, MainWindowViewModel.QuietPeriodFor(5, 0));
        Assert.Equal(MainWindowViewModel.LiveQuietPeriod, MainWindowViewModel.QuietPeriodFor(5, 6));
        Assert.Equal(MainWindowViewModel.LiveQuietPeriod, MainWindowViewModel.QuietPeriodFor(5, 5));
        Assert.Equal(MainWindowViewModel.LiveQuietPeriod, MainWindowViewModel.QuietPeriodFor(0, 12));
        Assert.True(MainWindowViewModel.EraseQuietPeriod > MainWindowViewModel.LiveQuietPeriod);
    }

    [Fact]
    public async Task ProvenanceUsesTappedOwner()
    {
        var q1 = Region("2+2=", y: 0);
        var q2 = Region("3+3=", y: 80);
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { q1, q2 }));
        AddInk(vm, q1, q2);
        await vm.RecognizeNowAsync();

        vm.ToggleAnswerProvenance(q2.Region.Id);

        Assert.Equal(q2.Region.StrokeIds.Order(), vm.ProvenanceStrokeIds.Order());
    }

    [Fact]
    public async Task V3LoadUsesCacheAsPreviousRebuildsGraphAndDoesNotBankOrReplay()
    {
        var q = Region("2+2=", y: 0);
        var bank = new RecordingBank();
        var recognizer = new EchoPreviousRecognizer();
        using MainWindowViewModel vm = Create(recognizer, bank);
        var persisted = new PersistedRegion(
            q.Region.Id, q.Region.StrokeIds, q.Region.Bounds,
            new PersistedRecognition(q.Result.Latex, q.Result.Tokens, q.Result.Confidence, q.Result.MinConfidence),
            new PersistedNodeResult("999", "999", true, "Numeric"));
        var document = new PenumbraDocument(
            q.Region.Groups.SelectMany(group => group.Strokes).ToArray(),
            new Dictionary<string, string>(), 3, new[] { persisted });

        await vm.LoadDocumentAsync(document);

        Assert.Single(recognizer.PreviousArguments.Single()!);
        Assert.Equal("4", vm.SheetNodes.Single().Result?.DisplayText);
        Assert.False(vm.AnswerLayer.Answers.Single().Play);
        Assert.Empty(bank.Captured);
    }

    [Fact]
    public async Task V3SerializeLoadReconstructsSameOwnerAndAuthoritativeResult()
    {
        var query = Region("2+2=", y: 0);
        using MainWindowViewModel source = Create(new QueueRecognizer(new[] { query }));
        AddInk(source, query);
        await source.RecognizeNowAsync();
        string json = PenumbraDocumentSerializer.Serialize(source.CreateDocumentSnapshot());

        using MainWindowViewModel loaded = Create(new EchoPreviousRecognizer());
        await loaded.LoadDocumentAsync(PenumbraDocumentSerializer.Deserialize(json));

        Assert.Equal(query.Region.Id, loaded.SheetNodes.Single().Id);
        Assert.Equal("4", loaded.SheetNodes.Single().Result?.DisplayText);
        Assert.Equal(query.Region.Id, loaded.AnswerLayer.Answers.Single().OwnerId);
        Assert.False(loaded.AnswerLayer.Answers.Single().Play);
    }

    [Fact]
    public async Task V2LoadIgnoresCacheAndPerformsFreshRecognition()
    {
        var q = Region("2+2=", y: 0);
        var recognizer = new QueueRecognizer(new[] { q });
        using MainWindowViewModel vm = Create(recognizer);
        var document = new PenumbraDocument(
            q.Region.Groups.SelectMany(group => group.Strokes).ToArray(),
            new Dictionary<string, string>(), 2, Array.Empty<PersistedRegion>());

        await vm.LoadDocumentAsync(document);

        Assert.Empty(recognizer.PreviousArguments.Single()!);
        Assert.Single(vm.SheetNodes);
    }

    [Fact]
    public async Task SaveSnapshotKeepsRecognitionAndNeutralResultButNoGraphEdges()
    {
        // Persistence shape is mechanically constrained by Core DTOs; this test guards App ownership.
        var q = Region("2+2=", y: 0);
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { q }));
        AddInk(vm, q);
        await vm.RecognizeNowAsync();

        PenumbraDocument saved = vm.CreateDocumentSnapshot();

        Assert.Equal(3, saved.Version);
        Assert.Equal(q.Region.Id, saved.Regions.Single().Id);
        Assert.Equal("4", saved.Regions.Single().NodeResult?.DisplayText);
    }

    [Fact]
    public void DependencyInjectionUsesOneRecognizerAndGraphPerViewModel()
    {
        using ServiceProvider provider = new ServiceCollection().AddPenumbraApp().BuildServiceProvider();
        IRecognizer legacy = provider.GetRequiredService<IRecognizer>();
        IRegionRecognizer regions = provider.GetRequiredService<IRegionRecognizer>();
        MainWindowViewModel first = provider.GetRequiredService<MainWindowViewModel>();
        MainWindowViewModel second = provider.GetRequiredService<MainWindowViewModel>();

        Assert.Same(legacy, regions);
        Assert.NotSame(first, second);
        Assert.NotSame(provider.GetRequiredService<SheetGraph>(), provider.GetRequiredService<SheetGraph>());
        first.Dispose();
        second.Dispose();
    }

    private static MainWindowViewModel Create(IRegionRecognizer recognizer, IGlyphBank? bank = null) => new(
        recognizer,
        new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
        bank,
        new HandwritingSynthesizer(new[] { new AnyGlyphSource() }),
        RecognitionCalibration.Default);

    private static RegionRecognition Region(
        string latex, double y, Guid? id = null, Guid? strokeId = null, bool dirty = true)
    {
        Guid sid = strokeId ?? Guid.NewGuid();
        var stroke = new Stroke(sid, new[]
        {
            new StrokeSample(10, y + 10, TimeSpan.Zero, .5),
            new StrokeSample(20, y + 30, TimeSpan.FromMilliseconds(20), .5),
        });
        var bounds = new InkBounds(10, y + 10, 20, 30);
        var token = new RecognizedToken("=", new[] { sid }, bounds, .99);
        var region = new InkRegion(id ?? Guid.NewGuid(), new[] { sid }, bounds, new[] { new StrokeGroup(new[] { stroke }, bounds) });
        return new RegionRecognition(region, new RecognitionResult(latex, new[] { token }, .99, .99), dirty);
    }

    private static void AddInk(MainWindowViewModel vm, params RegionRecognition[] regions)
    {
        vm.LiveRecognition = false;
        foreach (Stroke stroke in regions.SelectMany(region => region.Region.Groups).SelectMany(group => group.Strokes))
        {
            if (vm.Document.Strokes.All(existing => existing.Id != stroke.Id)) vm.Document.AddStroke(stroke);
        }
    }

    private sealed class QueueRecognizer(params IReadOnlyList<RegionRecognition>[] results) : IRegionRecognizer
    {
        private readonly Queue<IReadOnlyList<RegionRecognition>> _results = new(results);
        public List<IReadOnlyList<RegionRecognition>?> PreviousArguments { get; } = new();
        public IReadOnlyList<RegionRecognition> RecognizeRegions(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default)
        {
            PreviousArguments.Add(previous);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class EchoPreviousRecognizer : IRegionRecognizer
    {
        public List<IReadOnlyList<RegionRecognition>?> PreviousArguments { get; } = new();
        public IReadOnlyList<RegionRecognition> RecognizeRegions(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default)
        {
            PreviousArguments.Add(previous);
            return Task.FromResult<IReadOnlyList<RegionRecognition>>(previous!.Select(region => region with
            {
                Region = region.Region with { Groups = new[] { new StrokeGroup(strokes, region.Region.Bounds) } },
                Dirty = false,
            }).ToArray());
        }
    }

    private sealed class ControlledRecognizer : IRegionRecognizer
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<RegionRecognition>>> _calls = new();
        public List<IReadOnlyList<RegionRecognition>?> PreviousArguments { get; } = new();
        public IReadOnlyList<RegionRecognition> RecognizeRegions(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default)
        {
            PreviousArguments.Add(previous);
            var source = new TaskCompletionSource<IReadOnlyList<RegionRecognition>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _calls.Add(source);
            return source.Task; // deliberately ignores cancellation to test generation enforcement
        }
        public void Complete(int index, IReadOnlyList<RegionRecognition> result) => _calls[index].SetResult(result);
        public async Task WaitForCallsAsync(int count)
        {
            for (int i = 0; i < 100 && _calls.Count < count; i++) await Task.Delay(1);
            Assert.True(_calls.Count >= count);
        }
    }

    private sealed class AnyGlyphSource : IGlyphSource
    {
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) => new[]
        {
            new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(0, 0, TimeSpan.Zero, .5),
                new StrokeSample(1, 1, TimeSpan.FromMilliseconds(20), .5),
            }),
        };
    }

    private sealed class RecordingBank : IGlyphBank
    {
        public List<GlyphSample> Captured { get; } = new();
        public void Capture(GlyphSample sample) => Captured.Add(sample);
        public bool Has(string symbol) => false;
        public IReadOnlyList<GlyphSample> Samples(string symbol) => Array.Empty<GlyphSample>();
        public GlyphSample? Sample(string symbol, Random random) => null;
    }
}
