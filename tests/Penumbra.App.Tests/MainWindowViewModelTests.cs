using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Penumbra.App;
using Penumbra.App.Services;
using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Core.Layout;
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
    public async Task UniqueEquationSolutionMaterializesForLaterVariableQuery()
    {
        RegionRecognition equation = ExpressionRegion("2x=4", 0, "2", "x", "=", "4");
        RegionRecognition query = ExpressionRegion("x=", 80, "x", "=");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { equation, query }));
        AddInk(vm, equation, query);

        await vm.RecognizeNowAsync();

        SheetNode queryNode = vm.SheetNodes.Single(node => node.Id == query.Region.Id);
        Assert.Equal("2", queryNode.Result?.DisplayText);
        Assert.Equal(query.Region.Id, Assert.Single(vm.AnswerLayer.Answers).OwnerId);
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
    public async Task MatchingV4LoadSeedsForcedRefreshHintRebuildsGraphAndDoesNotBankOrReplay()
    {
        var q = ExpressionRegion("2+2=", y: 0, "2", "+", "2", "=");
        var bank = new RecordingBank();
        var recognizer = new EchoPreviousRecognizer();
        using MainWindowViewModel vm = Create(recognizer, bank);
        Stroke[] strokes = q.Region.Groups.SelectMany(group => group.Strokes).ToArray();
        var persisted = new PersistedRegion(
            q.Region.Id, q.Region.StrokeIds, q.Region.Bounds,
            new PersistedRecognition(q.Result.Latex, q.Result.Tokens, q.Result.Confidence, q.Result.MinConfidence),
            new PersistedNodeResult("999", "999", true, "Numeric"));
        var document = new PenumbraDocument(
            strokes,
            new Dictionary<string, string>(),
            PenumbraDocumentSerializer.SchemaVersion,
            new[] { persisted },
            strokes.Select(stroke => new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk)).ToArray(),
            RecognitionPipelineFingerprint.Current);

        await vm.LoadDocumentAsync(document);

        RegionRecognition hint = Assert.Single(recognizer.PreviousArguments.Single()!);
        Assert.True(hint.RequiresAuthoritativeRecognition);
        Assert.Equal("4", vm.SheetNodes.Single().Result?.DisplayText);
        Assert.False(vm.AnswerLayer.Answers.Single().Play);
        Assert.Empty(bank.Captured);
    }

    [Fact]
    public async Task V4SerializeLoadReconstructsSameOwnerAndAuthoritativeResult()
    {
        var query = ExpressionRegion("2+2=", y: 0, "2", "+", "2", "=");
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
        var q = ExpressionRegion("2+2=", 0, "2", "+", "2", "=");
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
    public async Task V3LoadIgnoresOtherwiseValidCacheAndPerformsFreshRecognition()
    {
        var q = Region("2+2=", y: 0);
        var recognizer = new QueueRecognizer(new[] { q });
        using MainWindowViewModel vm = Create(recognizer);
        var persisted = new PersistedRegion(
            q.Region.Id,
            q.Region.StrokeIds,
            q.Region.Bounds,
            new PersistedRecognition(q.Result.Latex, q.Result.Tokens, q.Result.Confidence, q.Result.MinConfidence));
        var document = new PenumbraDocument(
            q.Region.Groups.SelectMany(group => group.Strokes).ToArray(),
            new Dictionary<string, string>(),
            3,
            new[] { persisted });

        await vm.LoadDocumentAsync(document);

        Assert.Empty(recognizer.PreviousArguments.Single()!);
        Assert.Single(vm.SheetNodes);
    }

    [Fact]
    public async Task V4MismatchedFingerprintInvalidatesCacheWithoutDisplacingRawInk()
    {
        var q = Region("2+2=", y: 0);
        var recognizer = new QueueRecognizer(new[] { q });
        using MainWindowViewModel vm = Create(recognizer);
        Stroke[] strokes = q.Region.Groups.SelectMany(group => group.Strokes).ToArray();
        var persisted = new PersistedRegion(
            q.Region.Id,
            q.Region.StrokeIds,
            q.Region.Bounds,
            new PersistedRecognition(q.Result.Latex, q.Result.Tokens, q.Result.Confidence, q.Result.MinConfidence));
        var document = new PenumbraDocument(
            strokes,
            new Dictionary<string, string>(),
            PenumbraDocumentSerializer.SchemaVersion,
            new[] { persisted },
            strokes.Select(stroke => new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk)).ToArray(),
            "obsolete-pipeline");

        await vm.LoadDocumentAsync(document);

        Assert.Equal(strokes.Select(stroke => stroke.Id), vm.Document.Strokes.Select(stroke => stroke.Id));
        Assert.Empty(recognizer.PreviousArguments.Single()!);
        Assert.Single(vm.SheetNodes);
    }

    [Fact]
    public async Task V4AmbiguousProvenanceInvalidatesCacheWithoutDisplacingRawInk()
    {
        var q = Region("2+2=", y: 0);
        var recognizer = new QueueRecognizer(new[] { q });
        using MainWindowViewModel vm = Create(recognizer);
        Stroke[] strokes = q.Region.Groups.SelectMany(group => group.Strokes).ToArray();
        var persisted = new PersistedRegion(
            q.Region.Id,
            q.Region.StrokeIds,
            q.Region.Bounds,
            new PersistedRecognition(q.Result.Latex, q.Result.Tokens, q.Result.Confidence, q.Result.MinConfidence));
        var duplicated = new PersistedStrokeMetadata(strokes[0].Id, StrokeOriginNames.UserInk);
        var document = new PenumbraDocument(
            strokes,
            new Dictionary<string, string>(),
            PenumbraDocumentSerializer.SchemaVersion,
            new[] { persisted },
            new[] { duplicated, duplicated },
            RecognitionPipelineFingerprint.Current);

        await vm.LoadDocumentAsync(document);

        Assert.Equal(strokes.Select(stroke => stroke.Id), vm.Document.Strokes.Select(stroke => stroke.Id));
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

        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, saved.Version);
        Assert.Equal(RecognitionPipelineFingerprint.Current, saved.RecognitionPipelineFingerprint);
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
        Assert.Same(NoOpLocalMetricsSink.Instance, provider.GetRequiredService<ILocalMetricsSink>());
        Assert.NotSame(first, second);
        Assert.NotSame(provider.GetRequiredService<SheetGraph>(), provider.GetRequiredService<SheetGraph>());
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void DependencyInjectionPreservesAPreRegisteredLocalMetricsSink()
    {
        var metrics = new BoundedInMemoryMetricsSink(8);
        var services = new ServiceCollection();
        services.AddSingleton<ILocalMetricsSink>(metrics);
        services.AddSingleton<IPageStore>(new NonWritingPageStore());

        using ServiceProvider provider = services.AddPenumbraApp().BuildServiceProvider();

        Assert.Same(metrics, provider.GetRequiredService<ILocalMetricsSink>());
        using MainWindowViewModel vm = provider.GetRequiredService<MainWindowViewModel>();
        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(10, 10, TimeSpan.Zero, .5),
        }));
        vm.NotifyStrokeStarted();

        SheetGraph sheet = provider.GetRequiredService<SheetGraph>();
        sheet.Upsert(Guid.NewGuid(), "1+1=");
        sheet.RecomputeDetailed();
        provider.GetRequiredService<IRegionRecognizer>().RecognizeRegions(Array.Empty<Stroke>());

        MetricOperation[] operations = metrics.Snapshot().Observations
            .Select(observation => observation.Operation)
            .ToArray();
        Assert.Contains(MetricOperation.RecognitionQuietPeriod, operations);
        Assert.Contains(MetricOperation.SheetRecompute, operations);
        Assert.Contains(MetricOperation.RecognitionPartition, operations);
        Assert.Contains(MetricOperation.RecognitionProcessing, operations);
    }

    // --- 5.3 A1: hold-to-grab stamp-as-ink, banking exclusion, drag-cancel re-signal ------------------

    [Fact]
    public async Task StampAnswerAddsOneUndoableBatchWithFreshIds()
    {
        var query = ExpressionRegion("2+2=", 0, "2", "+", "2", "=");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { query }));
        AddInk(vm, query);
        await vm.RecognizeNowAsync();

        AnswerAnimation answer = vm.AnswerLayer.Answers.Single();
        IReadOnlyList<Stroke> answerStrokes = answer.Handwriting.Strokes;
        int answerCount = answerStrokes.Count;
        Assert.True(answerCount > 0);
        HashSet<Guid> answerIds = answerStrokes.Select(stroke => stroke.Id).ToHashSet();
        HashSet<Guid> before = vm.Document.Strokes.Select(stroke => stroke.Id).ToHashSet();

        vm.StampAnswer(answer.OwnerId, dx: 40, dy: 120, dropX: 40, dropY: 300);

        // The document gained exactly the answer's strokes...
        Assert.Equal(before.Count + answerCount, vm.Document.Strokes.Count);
        Guid[] stamped = vm.Document.Strokes.Select(stroke => stroke.Id).Where(id => !before.Contains(id)).ToArray();
        Assert.Equal(answerCount, stamped.Length);
        Assert.All(stamped, id => Assert.Equal(
            StrokeOriginKind.SynthesizedInk,
            vm.Document.GetStrokeOrigin(id)));
        // ...with fresh ids, disjoint from the answer's own (an id reuse would corrupt Seam-1 alignment)...
        Assert.All(stamped, id => Assert.DoesNotContain(id, answerIds));
        // ...and the whole batch is a single undo step.
        vm.Document.Undo();
        Assert.Equal(before.Count, vm.Document.Strokes.Count);
    }

    [Fact]
    public async Task StampedStrokesAreNeverBanked()
    {
        var query = Region("2+2=", y: 0);
        var bank = new RecordingBank();
        var recognizer = new FuncRecognizer { Next = _ => new[] { query } };
        using MainWindowViewModel vm = Create(recognizer, bank);
        AddInk(vm, query);
        await vm.RecognizeNowAsync();

        Guid ownerId = vm.AnswerLayer.Answers.Single().OwnerId;
        HashSet<Guid> beforeStamp = vm.Document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        vm.StampAnswer(ownerId, dx: 0, dy: 0, dropX: 0, dropY: 0);
        Guid[] stampedIds = vm.Document.Strokes.Select(stroke => stroke.Id).Where(id => !beforeStamp.Contains(id)).ToArray();
        Assert.NotEmpty(stampedIds);

        // A fresh, hand-drawn digit stroke stands as the positive control — real ink banking must still work.
        var digitStroke = new Stroke(Guid.NewGuid(), new[] { new StrokeSample(30, 10, TimeSpan.Zero, .5) });
        vm.Document.AddStroke(digitStroke);

        // A later completed query whose "4" is backed by the STAMPED stroke and whose "1" is backed by the
        // fresh real ink. Banking must take the real digit and skip the stamped one (D10).
        Guid originalId = query.Region.StrokeIds[0];
        var stampedToken = new RecognizedToken("4", new[] { stampedIds[0] }, new InkBounds(0, 0, 20, 30), .99);
        var realToken = new RecognizedToken("1", new[] { digitStroke.Id }, new InkBounds(30, 0, 20, 30), .99);
        var eqToken = new RecognizedToken("=", new[] { originalId }, new InkBounds(60, 0, 20, 30), .99);
        var completed = new RegionRecognition(
            new InkRegion(query.Region.Id, new[] { originalId, digitStroke.Id, stampedIds[0] }, new InkBounds(0, 0, 80, 30), Array.Empty<StrokeGroup>()),
            new RecognitionResult("4+1=", new[] { stampedToken, realToken, eqToken }, .99, .99),
            Dirty: true);
        recognizer.Next = _ => new[] { completed };

        await vm.RecognizeNowAsync();

        Assert.Contains(bank.Captured, sample => sample.Symbol == "1");
        Assert.DoesNotContain(bank.Captured, sample => sample.Strokes.Any(stroke => stampedIds.Contains(stroke.Id)));
    }

    [Fact]
    public async Task StampedStrokesRemainExcludedFromBankingAfterSerializeAndReopen()
    {
        var query = Region("2+2=", y: 0);
        using MainWindowViewModel source = Create(new QueueRecognizer(new[] { query }));
        AddInk(source, query);
        await source.RecognizeNowAsync();

        HashSet<Guid> beforeStamp = source.Document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        source.StampAnswer(
            source.AnswerLayer.Answers.Single().OwnerId,
            dx: 0,
            dy: 0,
            dropX: 0,
            dropY: 0);
        Guid stampedId = source.Document.Strokes
            .Select(stroke => stroke.Id)
            .First(id => !beforeStamp.Contains(id));
        var realStroke = new Stroke(
            Guid.NewGuid(),
            new[] { new StrokeSample(30, 10, TimeSpan.Zero, .5) });
        source.Document.AddStroke(realStroke);
        PenumbraDocument reopenedDocument = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(source.CreateDocumentSnapshot()));

        Guid originalId = query.Region.StrokeIds[0];
        var completed = new RegionRecognition(
            new InkRegion(
                query.Region.Id,
                new[] { originalId, realStroke.Id, stampedId },
                new InkBounds(0, 0, 80, 30),
                Array.Empty<StrokeGroup>()),
            new RecognitionResult(
                "4+1=",
                new[]
                {
                    new RecognizedToken("4", new[] { stampedId }, new InkBounds(0, 0, 20, 30), .99),
                    new RecognizedToken("1", new[] { realStroke.Id }, new InkBounds(30, 0, 20, 30), .99),
                    new RecognizedToken("=", new[] { originalId }, new InkBounds(60, 0, 20, 30), .99),
                },
                .99,
                .99),
            Dirty: true);
        var bank = new RecordingBank();
        var recognizer = new FuncRecognizer { Next = _ => new[] { query } };
        using MainWindowViewModel reopened = Create(recognizer, bank);

        await reopened.LoadDocumentAsync(reopenedDocument);
        Assert.Equal(StrokeOriginKind.SynthesizedInk, reopened.Document.GetStrokeOrigin(stampedId));
        Assert.Equal(StrokeOriginKind.UserInk, reopened.Document.GetStrokeOrigin(realStroke.Id));
        recognizer.Next = _ => new[] { completed };
        await reopened.RecognizeNowAsync();

        Assert.Contains(bank.Captured, sample => sample.Symbol == "1");
        Assert.DoesNotContain(bank.Captured, sample => sample.Strokes.Any(stroke => stroke.Id == stampedId));
    }

    [Fact]
    public void AnswerDragCancelReSignalsDebouncer()
    {
        var time = new FakeTimeProvider();
        var recognizer = new CountingRecognizer();
        using MainWindowViewModel vm = Create(recognizer, time: time);
        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[] { new StrokeSample(10, 10, TimeSpan.Zero, .5) }));

        // The grab's pen-down cancelled the pending live read; a cancelled drag then mutates nothing, so on
        // its own the eaten pass never restarts.
        vm.NotifyStrokeStarted();
        time.Advance(LiveQuietPeriodPlus());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, recognizer.Calls);

        // The drag-cancel notification restores it.
        vm.NotifyAnswerDragCancelled();
        time.Advance(LiveQuietPeriodPlus());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, recognizer.Calls);
        Assert.Equal(0, time.TimestampReads); // default no-op metrics never touch the monotonic clock
    }

    [Fact]
    public void QuietPeriodMetricsSeparateSupersededAndCompletedSignals()
    {
        var time = new FakeTimeProvider();
        var metrics = new BoundedInMemoryMetricsSink(8);
        var recognizer = new CountingRecognizer();
        using MainWindowViewModel vm = Create(recognizer, time: time, metrics: metrics);

        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(10, 10, TimeSpan.Zero, .5),
        }));
        time.Advance(TimeSpan.FromMilliseconds(400));
        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(20, 10, TimeSpan.Zero, .5),
        }));
        time.Advance(LiveQuietPeriodPlus());
        Dispatcher.UIThread.RunJobs();

        MetricObservation[] quiet = metrics.Snapshot().Observations
            .Where(observation => observation.Operation == MetricOperation.RecognitionQuietPeriod)
            .ToArray();
        Assert.Equal(2, quiet.Length);
        Assert.Equal(MetricOutcome.Cancelled, quiet[0].Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(400), quiet[0].Duration);
        Assert.Equal(MetricOutcome.Completed, quiet[1].Outcome);
        Assert.Equal(MainWindowViewModel.LiveQuietPeriod, quiet[1].Duration);
        Assert.All(quiet, observation => Assert.Null(observation.ItemCount));
        Assert.Equal(1, recognizer.Calls);
    }

    [Fact]
    public void EnabledMetrics_DueCallbackRacingReplacementSignal_DoesNotFireEarly()
    {
        using var completedEntered = new ManualResetEventSlim();
        using var releaseCompletion = new ManualResetEventSlim();
        using var dueFinished = new ManualResetEventSlim();
        var time = new FakeTimeProvider();
        var metrics = new BlockingCompletedQuietSink(completedEntered, releaseCompletion);
        var recognizer = new CountingRecognizer();
        using MainWindowViewModel vm = Create(recognizer, time: time, metrics: metrics);
        Dispatcher.UIThread.RunJobs(); // Establish dispatcher ownership before the timer-thread callback.
        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(10, 10, TimeSpan.Zero, .5),
        }));

        Exception? dueFailure = null;
        _ = Task.Run(() =>
        {
            try
            {
                time.Advance(LiveQuietPeriodPlus());
            }
            catch (Exception exception)
            {
                dueFailure = exception;
            }
            finally
            {
                dueFinished.Set();
            }
        });
        Assert.True(completedEntered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
            {
                new StrokeSample(20, 10, TimeSpan.Zero, .5),
            }));
        }
        finally
        {
            releaseCompletion.Set();
        }

        Assert.True(dueFinished.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(dueFailure);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, recognizer.Calls);

        time.Advance(LiveQuietPeriodPlus());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, recognizer.Calls);
        MetricObservation[] quiet = metrics.Snapshot().Observations
            .Where(observation => observation.Operation == MetricOperation.RecognitionQuietPeriod)
            .ToArray();
        Assert.Equal(2, quiet.Length);
        Assert.All(quiet, observation => Assert.Equal(MetricOutcome.Completed, observation.Outcome));
    }

    [Fact]
    public void StrokeStartCancelsPendingQuietMetricExactlyOnce()
    {
        var time = new FakeTimeProvider();
        var metrics = new BoundedInMemoryMetricsSink(4);
        var recognizer = new CountingRecognizer();
        using MainWindowViewModel vm = Create(recognizer, time: time, metrics: metrics);
        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(10, 10, TimeSpan.Zero, .5),
        }));
        time.Advance(TimeSpan.FromMilliseconds(250));

        vm.NotifyStrokeStarted();
        time.Advance(LiveQuietPeriodPlus());
        Dispatcher.UIThread.RunJobs();

        MetricObservation quiet = Assert.Single(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.RecognitionQuietPeriod);
        Assert.Equal(MetricOutcome.Cancelled, quiet.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(250), quiet.Duration);
        Assert.Equal(0, recognizer.Calls);
    }

    [Fact]
    public async Task StampOntoLineRescalesToLineHeight()
    {
        var query = Region("2+2=", y: 0);
        // A separate accepted line to drop onto, with a known glyph height (60 → clamped median 60).
        var targetStroke = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(10, 410, TimeSpan.Zero, .5),
            new StrokeSample(30, 470, TimeSpan.FromMilliseconds(20), .5),
        });
        var targetBounds = new InkBounds(10, 400, 40, 60);
        var targetToken = new RecognizedToken("7", new[] { targetStroke.Id }, targetBounds, .99);
        var target = new RegionRecognition(
            new InkRegion(Guid.NewGuid(), new[] { targetStroke.Id }, targetBounds, new[] { new StrokeGroup(new[] { targetStroke }, targetBounds) }),
            new RecognitionResult("7", new[] { targetToken }, .99, .99),
            Dirty: true);

        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { query, target }));
        AddInk(vm, query, target);
        await vm.RecognizeNowAsync();

        AnswerAnimation answer = vm.AnswerLayer.Answers.Single(a => a.OwnerId == query.Region.Id);
        double sourceHeight = StrokeHeight(answer.Handwriting.Strokes);
        double lineHeight = MainWindowViewModel.ClampedMedianTokenHeight(target.Result.Tokens);
        Assert.True(Math.Abs(sourceHeight - lineHeight) > 1);   // a meaningful rescale, not a coincidence

        // Dropped on the target line's y-band → rescaled so its glyph height matches that line's.
        int before = vm.Document.Strokes.Count;
        vm.StampAnswer(query.Region.Id, dx: 0, dy: 0, dropX: 80, dropY: 430);
        Stroke[] onLine = vm.Document.Strokes.Skip(before).ToArray();
        Assert.Equal(lineHeight, StrokeHeight(onLine), 3);

        // Dropped into empty space → keeps the answer's own size.
        int before2 = vm.Document.Strokes.Count;
        vm.StampAnswer(query.Region.Id, dx: 0, dy: 0, dropX: 30, dropY: 1000);
        Stroke[] empty = vm.Document.Strokes.Skip(before2).ToArray();
        Assert.Equal(sourceHeight, StrokeHeight(empty), 3);
    }

    // --- 5.3 A2: taffy probe loop ---------------------------------------------------------------

    [Fact]
    public async Task LiteralRunLayerPublishesFractionLiteralsFromIndependentTreeNodes()
    {
        RegionRecognition fraction = FractionQueryRegion();
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { fraction }));
        AddInk(vm, fraction);

        await vm.RecognizeNowAsync();

        LiteralRunOwner owner = Assert.Single(vm.LiteralRunLayer.Owners);
        Assert.Equal(fraction.Region.Id, owner.OwnerId);
        Assert.Equal(new[] { "1", "2" }, owner.Runs.Select(run => run.ValueText));
        Assert.DoesNotContain(owner.Runs, run => run.ValueText == "12");
    }

    [Fact]
    public async Task TaffyProbeShowsLiteralAndDependentGhostsWithoutMutatingCommittedState()
    {
        var metrics = new BoundedInMemoryMetricsSink(16);
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        var query = ExpressionRegion("x+1=", 80, "x", "+", "1", "=");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { definition, query }), metrics: metrics);
        AddInk(vm, definition, query);
        await vm.RecognizeNowAsync();

        LiteralRun run = vm.LiteralRunLayer.Owners
            .Single(owner => owner.OwnerId == definition.Region.Id)
            .Runs.Single(candidate => candidate.ValueText == "5");
        Stroke[] documentBefore = vm.Document.Strokes.ToArray();
        EvaluationResult? definitionBefore = vm.SheetNodes.Single(n => n.Id == definition.Region.Id).Result;
        EvaluationResult? queryBefore = vm.SheetNodes.Single(n => n.Id == query.Region.Id).Result;
        AnswerLayer answersBefore = vm.AnswerLayer;

        Assert.True(vm.BeginTaffy(definition.Region.Id, run));
        vm.UpdateTaffy(screenDx: 28); // 5 + two 14 px steps = 7; dependent query becomes 8.

        TaffyGhostLayer layer = Assert.IsType<TaffyGhostLayer>(vm.TaffyGhostLayer);
        Assert.Equal("7", layer.Ghosts.Single(ghost => ghost.IsLiteral).ValueText);
        Assert.Equal("8", layer.Ghosts.Single(ghost => !ghost.IsLiteral && ghost.OwnerId == query.Region.Id).ValueText);
        Assert.True(run.SourceStrokeIds.All(layer.MutedStrokeIds.Contains));
        Assert.Contains(query.Region.Id, layer.HiddenAnswerOwnerIds);
        Assert.Same(answersBefore, vm.AnswerLayer);
        Assert.Equal(documentBefore, vm.Document.Strokes);
        Assert.Equal(definitionBefore, vm.SheetNodes.Single(n => n.Id == definition.Region.Id).Result);
        Assert.Equal(queryBefore, vm.SheetNodes.Single(n => n.Id == query.Region.Id).Result);
        Assert.Equal(1, vm.TaffyProbeCount);

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
    public async Task MissingTaffyGlyphRecordsRefusalAndPublishesAnEmptyFrame()
    {
        var metrics = new BoundedInMemoryMetricsSink(8);
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        using var vm = new MainWindowViewModel(
            new QueueRecognizer(new[] { definition }),
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            glyphBank: null,
            synthesizer: new HandwritingSynthesizer(Array.Empty<IGlyphSource>()),
            calibration: RecognitionCalibration.Default,
            time: TimeProvider.System,
            metrics: metrics);
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");

        Assert.True(vm.BeginTaffy(definition.Region.Id, run));

        MetricObservation synthesis = Assert.Single(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.TaffyGhostSynthesis);
        MetricObservation publication = Assert.Single(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.TaffyPublication);
        Assert.Equal(MetricOutcome.Refused, synthesis.Outcome);
        Assert.Equal(0, synthesis.ItemCount);
        Assert.Equal(MetricOutcome.Completed, publication.Outcome);
        Assert.Equal(0, publication.ItemCount);
        Assert.Empty(vm.TaffyGhostLayer!.Ghosts);
    }

    [Fact]
    public async Task TaffySynthesisFailureIsRecordedAndRethrownUnchanged()
    {
        var metrics = new BoundedInMemoryMetricsSink(8);
        var expected = new InvalidOperationException("synthesis sentinel");
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        using var vm = new MainWindowViewModel(
            new QueueRecognizer(new[] { definition }),
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            glyphBank: null,
            synthesizer: new HandwritingSynthesizer(new[] { new ThrowingGlyphSource(expected) }),
            calibration: RecognitionCalibration.Default,
            time: TimeProvider.System,
            metrics: metrics);
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => vm.BeginTaffy(definition.Region.Id, run));

        Assert.Same(expected, actual);
        MetricObservation synthesis = Assert.Single(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.TaffyGhostSynthesis);
        Assert.Equal(MetricOutcome.Failed, synthesis.Outcome);
        Assert.Null(synthesis.ItemCount);
        Assert.DoesNotContain(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.TaffyPublication);
    }

    [Fact]
    public async Task TaffyInternalCancellationWithoutCallerTokenRecordsFailedAndRethrowsUnchanged()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        var metrics = new BoundedInMemoryMetricsSink(8);
        var time = new FakeTimeProvider();
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        using var vm = new MainWindowViewModel(
            new QueueRecognizer(new[] { definition }),
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            glyphBank: null,
            synthesizer: new HandwritingSynthesizer(new[] { new ThrowingSymbolGlyphSource("6", expected) }),
            calibration: RecognitionCalibration.Default,
            time: time,
            metrics: metrics);
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");
        Assert.True(vm.BeginTaffy(definition.Region.Id, run));

        OperationCanceledException actual = Assert.Throws<OperationCanceledException>(
            () => vm.UpdateTaffy(screenDx: 14));

        Assert.Same(expected, actual);
        Assert.Equal(
            new[] { MetricOutcome.Completed, MetricOutcome.Failed },
            metrics.Snapshot().Observations
                .Where(observation => observation.Operation == MetricOperation.TaffyGhostSynthesis)
                .Select(observation => observation.Outcome));
        MetricObservation processing = Assert.Single(
            metrics.Snapshot().Observations,
            observation => observation.Operation == MetricOperation.TaffyProcessing);
        Assert.Equal(MetricOutcome.Failed, processing.Outcome);
        Assert.DoesNotContain(
            metrics.Snapshot().Observations,
            observation => observation.Outcome == MetricOutcome.Cancelled);
    }

    [Fact]
    public async Task TaffyUsesCumulativeDxAndSkipsUnchangedOrRateLimitedValues()
    {
        var time = new FakeTimeProvider();
        var metrics = new BoundedInMemoryMetricsSink(16);
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        using MainWindowViewModel vm = Create(
            new QueueRecognizer(new[] { definition }),
            time: time,
            metrics: metrics);
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");

        Assert.True(vm.BeginTaffy(definition.Region.Id, run));
        vm.UpdateTaffy(screenDx: 13); // Not one complete step.
        Assert.Equal(0, vm.TaffyProbeCount);

        vm.UpdateTaffy(screenDx: 28); // Original 5 -> 7.
        Assert.Equal(1, vm.TaffyProbeCount);
        Assert.Equal("7", vm.TaffyGhostLayer!.Ghosts.Single(ghost => ghost.IsLiteral).ValueText);

        vm.UpdateTaffy(screenDx: 14); // Would be original 5 -> 6, but the 33 ms floor suppresses it.
        Assert.Equal(1, vm.TaffyProbeCount);

        time.Advance(MainWindowViewModel.TaffyProbeFloor + TimeSpan.FromMilliseconds(1));
        vm.UpdateTaffy(screenDx: 14);

        Assert.Equal(2, vm.TaffyProbeCount);
        Assert.Equal("6", vm.TaffyGhostLayer!.Ghosts.Single(ghost => ghost.IsLiteral).ValueText);

        MetricObservation[] processing = metrics.Snapshot().Observations
            .Where(observation => observation.Operation == MetricOperation.TaffyProcessing)
            .ToArray();
        Assert.Equal(
            new[]
            {
                MetricOutcome.Refused,
                MetricOutcome.Completed,
                MetricOutcome.Refused,
                MetricOutcome.Completed,
            },
            processing.Select(observation => observation.Outcome));
        Assert.Equal(new int?[] { null, 1, null, 1 }, processing.Select(observation => observation.ItemCount));
    }

    [Fact]
    public async Task TaffyReusesDeterministicGhostGeometryWhenAValueRepeats()
    {
        var time = new FakeTimeProvider();
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { definition }), time: time);
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");

        Assert.True(vm.BeginTaffy(definition.Region.Id, run));
        vm.UpdateTaffy(screenDx: 28);
        SynthesizedHandwriting first = vm.TaffyGhostLayer!.Ghosts.Single(ghost => ghost.IsLiteral).Handwriting;

        time.Advance(MainWindowViewModel.TaffyProbeFloor + TimeSpan.FromMilliseconds(1));
        vm.UpdateTaffy(screenDx: 14);
        time.Advance(MainWindowViewModel.TaffyProbeFloor + TimeSpan.FromMilliseconds(1));
        vm.UpdateTaffy(screenDx: 28);

        SynthesizedHandwriting repeated = vm.TaffyGhostLayer!.Ghosts.Single(ghost => ghost.IsLiteral).Handwriting;
        Assert.Same(first, repeated);
    }

    [Fact]
    public async Task EndingTaffyRestoresPresentationAndPreservesRecognitionRoundTripState()
    {
        var time = new FakeTimeProvider();
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        var recognizer = new QueueRecognizer(new[] { definition }, new[] { definition with { Dirty = false } });
        using MainWindowViewModel vm = Create(recognizer, time: time);
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");
        Stroke[] documentBefore = vm.Document.Strokes.ToArray();
        bool couldUndoBefore = vm.Document.CanUndo;

        Assert.True(vm.BeginTaffy(definition.Region.Id, run));
        vm.UpdateTaffy(screenDx: 14);
        vm.EndTaffy();

        Assert.False(vm.IsTaffyActive);
        Assert.Null(vm.TaffyGhostLayer);
        Assert.Equal(documentBefore, vm.Document.Strokes);
        Assert.Equal(couldUndoBefore, vm.Document.CanUndo);

        await vm.RecognizeNowAsync();

        Assert.Equal(2, recognizer.PreviousArguments.Count);
        RegionRecognition previous = Assert.Single(recognizer.PreviousArguments[1]!);
        Assert.Equal(definition.Region.Id, previous.Region.Id);
        Assert.Equal("x=5", previous.Result.Latex);
    }

    [Fact]
    public async Task DocumentMutationForceEndsTaffy()
    {
        var definition = ExpressionRegion("x=5", 0, "x", "=", "5");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { definition }));
        AddInk(vm, definition);
        await vm.RecognizeNowAsync();
        LiteralRun run = vm.LiteralRunLayer.Owners.Single().Runs.Single(candidate => candidate.ValueText == "5");

        Assert.True(vm.BeginTaffy(definition.Region.Id, run));
        vm.UpdateTaffy(screenDx: -84); // A negative trial exercises the parenthesized splice path.
        Assert.Equal("-1", vm.TaffyGhostLayer!.Ghosts.Single(ghost => ghost.IsLiteral).ValueText);

        vm.Document.AddStroke(new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(500, 500, TimeSpan.Zero, .5),
        }));

        Assert.False(vm.IsTaffyActive);
        Assert.Null(vm.TaffyGhostLayer);
        Assert.Equal("5", vm.SheetNodes.Single().Result?.DisplayText);
    }

    // --- s23 dogfood closures: identity answers and safe stamp targeting -----------------------

    [Fact]
    public async Task UnchangedSymbolicQueriesDoNotEchoThemselvesAsAnswers()
    {
        var bare = ExpressionRegion("y=", 0, "y", "=");
        var unchanged = ExpressionRegion("y-2=", 80, "y", "-", "2", "=");
        var simplified = ExpressionRegion("y+y=", 160, "y", "+", "y", "=");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { bare, unchanged, simplified }));
        AddInk(vm, bare, unchanged, simplified);

        await vm.RecognizeNowAsync();

        Assert.DoesNotContain(vm.AnswerLayer.Answers, answer => answer.OwnerId == bare.Region.Id);
        Assert.DoesNotContain(vm.AnswerLayer.Answers, answer => answer.OwnerId == unchanged.Region.Id);
        Assert.Contains(vm.AnswerLayer.Answers, answer => answer.OwnerId == simplified.Region.Id);
    }

    [Fact]
    public async Task StampDirectlyOnLiteralAtomicallyReplacesItsSourceInk()
    {
        var source = ExpressionRegion("5-3=", 0, "5", "-", "3", "=");
        var target = ExpressionRegion("y=8", 80, "y", "=", "8");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { source, target }));
        AddInk(vm, source, target);
        await vm.RecognizeNowAsync();

        AnswerAnimation answer = vm.AnswerLayer.Answers.Single(a => a.OwnerId == source.Region.Id);
        LiteralRun targetRun = vm.LiteralRunLayer.Owners
            .Single(owner => owner.OwnerId == target.Region.Id)
            .Runs.Single(run => run.ValueText == "8");
        Stroke[] before = vm.Document.Strokes.ToArray();
        (double answerX, double answerY) = StrokeCenter(answer.Handwriting.Strokes);
        double targetX = targetRun.UnionBounds.X + targetRun.UnionBounds.Width / 2;
        double targetY = targetRun.UnionBounds.Y + targetRun.UnionBounds.Height / 2;

        vm.StampAnswer(source.Region.Id, targetX - answerX, targetY - answerY, targetX, targetY);

        Assert.True(targetRun.SourceStrokeIds.All(id => vm.Document.Strokes.All(stroke => stroke.Id != id)));
        Assert.True(vm.Document.Strokes.Count >= before.Length - targetRun.SourceStrokeIds.Count);

        vm.Document.Undo();
        Assert.Equal(before.Select(stroke => stroke.Id), vm.Document.Strokes.Select(stroke => stroke.Id));
    }

    [Fact]
    public async Task StampOnItsSourceLineHidesCommittedAnswerImmediately()
    {
        var query = ExpressionRegion("5+7=", 0, "5", "+", "7", "=");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { query }));
        AddInk(vm, query);
        await vm.RecognizeNowAsync();
        AnswerAnimation answer = vm.AnswerLayer.Answers.Single();

        double dropX = query.Region.Bounds.X + query.Region.Bounds.Width + 10;
        double dropY = query.Region.Bounds.Y + query.Region.Bounds.Height / 2;
        vm.StampAnswer(answer.OwnerId, dx: 0, dy: 0, dropX, dropY);

        Assert.Empty(vm.AnswerLayer.Answers);
    }

    [Fact]
    public async Task FarHorizontalDropAlignedWithExistingLineIsRejectedInsteadOfMerging()
    {
        var query = ExpressionRegion("5+7=", 0, "5", "+", "7", "=");
        using MainWindowViewModel vm = Create(new QueueRecognizer(new[] { query }));
        AddInk(vm, query);
        await vm.RecognizeNowAsync();
        AnswerAnimation answer = vm.AnswerLayer.Answers.Single();
        int before = vm.Document.Strokes.Count;

        vm.StampAnswer(answer.OwnerId, dx: 900, dy: 0, dropX: 1000, dropY: 25);

        Assert.Equal(before, vm.Document.Strokes.Count);
        Assert.Contains("move vertically", vm.RecognitionText, StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan LiveQuietPeriodPlus() =>
        MainWindowViewModel.LiveQuietPeriod + TimeSpan.FromMilliseconds(10);

    private static double StrokeHeight(IReadOnlyList<Stroke> strokes)
    {
        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (StrokeSample sample in strokes.SelectMany(stroke => stroke.Samples))
        {
            min = Math.Min(min, sample.Y);
            max = Math.Max(max, sample.Y);
        }

        return max - min;
    }

    private static (double X, double Y) StrokeCenter(IReadOnlyList<Stroke> strokes)
    {
        StrokeSample[] samples = strokes.SelectMany(stroke => stroke.Samples).ToArray();
        return ((samples.Min(sample => sample.X) + samples.Max(sample => sample.X)) / 2,
            (samples.Min(sample => sample.Y) + samples.Max(sample => sample.Y)) / 2);
    }

    private static MainWindowViewModel Create(
        IRegionRecognizer recognizer,
        IGlyphBank? bank = null,
        TimeProvider? time = null,
        ILocalMetricsSink? metrics = null) => new(
        recognizer,
        new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
        bank,
        new HandwritingSynthesizer(new[] { new AnyGlyphSource() }),
        RecognitionCalibration.Default,
        time,
        metrics);

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

    private static RegionRecognition ExpressionRegion(string latex, double y, params string[] labels)
    {
        var strokes = new List<Stroke>(labels.Length);
        var tokens = new List<RecognizedToken>(labels.Length);
        for (int i = 0; i < labels.Length; i++)
        {
            Guid strokeId = Guid.NewGuid();
            double x = 10 + i * 28;
            var stroke = new Stroke(strokeId, new[]
            {
                new StrokeSample(x, y + 10, TimeSpan.Zero, .5),
                new StrokeSample(x + 16, y + 40, TimeSpan.FromMilliseconds(20), .5),
            });
            var bounds = new InkBounds(x, y + 10, 16, 30);
            strokes.Add(stroke);
            tokens.Add(new RecognizedToken(labels[i], new[] { strokeId }, bounds, .99));
        }

        InkBounds regionBounds = new(10, y + 10, Math.Max(16, labels.Length * 28 - 12), 30);
        var groups = strokes.Zip(tokens, (stroke, token) => new StrokeGroup(new[] { stroke }, token.Bounds)).ToArray();
        var region = new InkRegion(Guid.NewGuid(), strokes.Select(stroke => stroke.Id).ToArray(), regionBounds, groups);
        return new RegionRecognition(region, new RecognitionResult(latex, tokens, .99, .99), Dirty: true);
    }

    private static RegionRecognition FractionQueryRegion()
    {
        Guid numeratorId = Guid.NewGuid();
        Guid denominatorId = Guid.NewGuid();
        Guid barId = Guid.NewGuid();
        Guid equalsId = Guid.NewGuid();
        Stroke[] strokes =
        [
            new Stroke(numeratorId,
            [
                new StrokeSample(14, 10, TimeSpan.Zero, .5),
                new StrokeSample(18, 20, TimeSpan.FromMilliseconds(20), .5),
            ]),
            new Stroke(denominatorId,
            [
                new StrokeSample(14, 32, TimeSpan.Zero, .5),
                new StrokeSample(18, 42, TimeSpan.FromMilliseconds(20), .5),
            ]),
            new Stroke(barId,
            [
                new StrokeSample(8, 26, TimeSpan.Zero, .5),
                new StrokeSample(24, 26, TimeSpan.FromMilliseconds(20), .5),
            ]),
            new Stroke(equalsId,
            [
                new StrokeSample(40, 20, TimeSpan.Zero, .5),
                new StrokeSample(50, 30, TimeSpan.FromMilliseconds(20), .5),
            ]),
        ];
        var numerator = new RecognizedToken("1", [numeratorId], new InkBounds(14, 10, 4, 10), .99);
        var denominator = new RecognizedToken("2", [denominatorId], new InkBounds(14, 32, 4, 10), .99);
        var bar = new RecognizedToken("-", [barId], new InkBounds(8, 26, 16, 1), .99);
        var equals = new RecognizedToken("=", [equalsId], new InkBounds(40, 20, 10, 10), .99);
        RecognizedToken[] tokens = [numerator, denominator, bar, equals];
        LayoutNode root = new RelationNode(
            new FractionNode(new LeafNode(numerator), new LeafNode(denominator), bar),
            equals,
            right: null);
        InkBounds bounds = new(8, 10, 42, 32);
        StrokeGroup[] groups = strokes.Zip(
            tokens,
            (stroke, token) => new StrokeGroup([stroke], token.Bounds)).ToArray();
        var region = new InkRegion(
            Guid.NewGuid(),
            strokes.Select(stroke => stroke.Id).ToArray(),
            bounds,
            groups);
        var result = new RecognitionResult(
            @"\frac{1}{2}=",
            tokens,
            .99,
            .99,
            LayoutParseOutcome.Accepted(root));
        return new RegionRecognition(region, result, Dirty: true);
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
                RequiresAuthoritativeRecognition = false,
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

    private sealed class NonWritingPageStore : IPageStore
    {
        public Task<PageSaveResult> SaveAsync(
            PenumbraDocument document,
            string path,
            long generation,
            PageSaveKind kind,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PageSaveResult(PageSaveStatus.Committed, generation));

        public Task<PageOpenResult> OpenAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PageOpenResult(PageOpenStatus.NotFound, null));
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

    private sealed class ThrowingGlyphSource(Exception exception) : IGlyphSource
    {
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) => throw exception;
    }

    private sealed class ThrowingSymbolGlyphSource(string throwOnSymbol, Exception exception) : IGlyphSource
    {
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random)
        {
            if (string.Equals(symbol, throwOnSymbol, StringComparison.Ordinal))
            {
                throw exception;
            }

            return new[]
            {
                new Stroke(Guid.NewGuid(), new[]
                {
                    new StrokeSample(0, 0, TimeSpan.Zero, .5),
                    new StrokeSample(1, 1, TimeSpan.FromMilliseconds(20), .5),
                }),
            };
        }
    }

    private sealed class RecordingBank : IGlyphBank
    {
        public List<GlyphSample> Captured { get; } = new();
        public void Capture(GlyphSample sample) => Captured.Add(sample);
        public bool Has(string symbol) => false;
        public IReadOnlyList<GlyphSample> Samples(string symbol) => Array.Empty<GlyphSample>();
        public GlyphSample? Sample(string symbol, Random random) => null;
    }

    // Returns whatever the current Next delegate maps the live strokes to — reconfigurable between passes,
    // so a test can build a result that references ids only known after an intervening stamp.
    private sealed class FuncRecognizer : IRegionRecognizer
    {
        public Func<IReadOnlyList<Stroke>, IReadOnlyList<RegionRecognition>> Next { get; set; }
            = _ => Array.Empty<RegionRecognition>();
        public IReadOnlyList<RegionRecognition> RecognizeRegions(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Next(strokes));
    }

    // Counts the reads it is asked to perform; used to observe that a debounced fire actually ran.
    private sealed class CountingRecognizer : IRegionRecognizer
    {
        public int Calls { get; private set; }
        public IReadOnlyList<RegionRecognition> RecognizeRegions(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(IReadOnlyList<Stroke> strokes, IReadOnlyList<RegionRecognition>? previous = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RegionRecognition>>(Array.Empty<RegionRecognition>());
        }
    }

    private sealed class BlockingCompletedQuietSink(
        ManualResetEventSlim completedEntered,
        ManualResetEventSlim releaseCompletion) : ILocalMetricsSink
    {
        private readonly BoundedInMemoryMetricsSink _inner = new(8);
        private int _blocked;

        public void Record(MetricObservation observation)
        {
            if (observation.Operation == MetricOperation.RecognitionQuietPeriod
                && observation.Outcome == MetricOutcome.Completed
                && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                completedEntered.Set();
                if (!releaseCompletion.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release the quiet-period metric sink.");
                }
            }

            _inner.Record(observation);
        }

        public LocalMetricsSnapshot Snapshot() => _inner.Snapshot();
    }

    // Minimal controllable clock, mirroring Penumbra.Core's DebouncerTests: one-shot timers fire in due
    // order as Advance walks past them. Enough surface for the live-recognition debouncer.
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = new();
        private readonly DateTimeOffset _origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _timestampReads;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public int TimestampReads => Volatile.Read(ref _timestampReads);

        public override long GetTimestamp()
        {
            Interlocked.Increment(ref _timestampReads);
            return (_now - _origin).Ticks;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime,
            };
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target = _now + by;
            while (true)
            {
                FakeTimer? next = _timers
                    .Where(t => !t.Disposed && t.Due is not null && t.Due <= target)
                    .OrderBy(t => t.Due)
                    .FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                _now = next.Due!.Value;
                next.Due = null;
                next.Fire();
            }

            _now = target;
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public FakeTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset? Due { get; set; }
            public bool Disposed { get; private set; }

            public void Fire() => _callback(_state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
