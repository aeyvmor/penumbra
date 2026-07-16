using System.Globalization;
using System.Runtime.InteropServices;
using Penumbra.App;
using Penumbra.App.ViewModels;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.MetricsHarness;

internal static class Program
{
    private const int DefaultIterations = 50;
    private const int DefaultWarmupIterations = 5;
    private const int MaximumIterations = 1_000;
    private const int MaximumWarmupIterations = 100;
    private static readonly TimeSpan TaffyThrottleAdvance = TimeSpan.FromMilliseconds(40);

    public static async Task<int> Main(string[] args)
    {
        if (!HarnessSettings.TryParse(args, out HarnessSettings settings))
        {
            Console.WriteLine(
                "usage=MetricsHarness [--iterations 1..1000] [--warmup 1..100]");
            return 2;
        }

        if (settings.ShowHelp)
        {
            Console.WriteLine(
                "usage=MetricsHarness [--iterations 1..1000] [--warmup 1..100]");
            return 0;
        }

        PrintHeader(settings);

        try
        {
            LocalMetricsSnapshot recognition = await RunRecognitionScenarioAsync(settings);
            LocalMetricsSnapshot sheet = RunSheetScenario(settings);
            LocalMetricsSnapshot taffy = await RunTaffyScenarioAsync(settings);
            LocalMetricsSnapshot persistence = await RunPersistenceScenarioAsync(settings);

            int warningCount = 0;
            warningCount += PrintRow(
                "recognition_synthetic_headless",
                recognition,
                MetricOperation.RecognitionProcessing,
                candidateBudgetMilliseconds: 250);
            warningCount += PrintRow(
                "recognition_synthetic_headless",
                recognition,
                MetricOperation.RecognitionPartition);
            warningCount += PrintRow(
                "recognition_synthetic_headless",
                recognition,
                MetricOperation.RecognitionClassification);
            warningCount += PrintRow(
                "recognition_synthetic_headless",
                recognition,
                MetricOperation.RecognitionGrammar,
                candidateBudgetMilliseconds: 25);
            warningCount += PrintRow(
                "sheet_synthetic_headless",
                sheet,
                MetricOperation.SheetRecompute,
                candidateBudgetMilliseconds: 100);
            warningCount += PrintRow(
                "taffy_synthetic_headless",
                taffy,
                MetricOperation.TaffyProcessing,
                candidateBudgetMilliseconds: 100);
            warningCount += PrintRow(
                "taffy_synthetic_headless",
                taffy,
                MetricOperation.TaffyProbe);
            warningCount += PrintRow(
                "taffy_synthetic_headless",
                taffy,
                MetricOperation.TaffyGhostSynthesis);
            warningCount += PrintRow(
                "taffy_synthetic_headless",
                taffy,
                MetricOperation.TaffyPublication);
            warningCount += PrintRow(
                "persistence_synthetic_local_filesystem",
                persistence,
                MetricOperation.ExplicitSave);
            warningCount += PrintRow(
                "persistence_synthetic_local_filesystem",
                persistence,
                MetricOperation.Autosave);
            warningCount += PrintRow(
                "persistence_synthetic_local_filesystem",
                persistence,
                MetricOperation.RecoveryRead);
            warningCount += PrintRow(
                "persistence_synthetic_local_filesystem",
                persistence,
                MetricOperation.CloseFlush);

            Console.WriteLine(warningCount == 0
                ? "overall=PASS"
                : "overall=PASS_WITH_LATENCY_WARNINGS");
            return 0;
        }
        catch (HarnessInvariantException exception)
        {
            Console.WriteLine($"overall=FAIL reason={exception.Code}");
            return 1;
        }
        catch
        {
            // Messages and stack traces can contain local paths or expression content. Keep failure output
            // deliberately fixed and privacy-safe; tests/build logs retain the diagnostic detail.
            Console.WriteLine("overall=FAIL reason=unexpected_execution_error");
            return 1;
        }
    }

    private static void PrintHeader(HarnessSettings settings)
    {
        Console.WriteLine("input_classification=SYNTHETIC/HEADLESS");
        Console.WriteLine(
            "result_scope=initial_machine_baseline_not_corpus_or_user_dogfood_acceptance");
#if DEBUG
        Console.WriteLine("build_configuration=DEBUG status=WARN");
#else
        Console.WriteLine("build_configuration=RELEASE status=PASS");
#endif
        Console.WriteLine($"runtime={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"os={RuntimeInformation.OSDescription}");
        Console.WriteLine($"logical_processors={Environment.ProcessorCount}");
        Console.WriteLine($"iterations={settings.Iterations} warmup={settings.WarmupIterations}");
    }

    private static async Task<LocalMetricsSnapshot> RunRecognitionScenarioAsync(
        HarnessSettings settings)
    {
        IReadOnlyList<Stroke> strokes = CreateRecognitionLine();
        using var classifier = new OnnxSymbolClassifier(
            Path.Combine(AppContext.BaseDirectory, "Models"));

        // Load/JIT/model warm-up happens with the singleton no-op before the measured sink exists.
        var warmupRecognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            classifier,
            NoOpLocalMetricsSink.Instance);
        for (int index = 0; index < settings.WarmupIterations; index++)
        {
            RecognitionResult result = await warmupRecognizer.RecognizeAsync(strokes);
            RequireRecognitionResult(result);
        }

        int expectedSamples = checked(settings.Iterations * 4);
        var sink = new BoundedInMemoryMetricsSink(checked(expectedSamples + 16));
        var recognizer = new ExpressionRecognizer(
            new OverlapStrokeSegmenter(),
            classifier,
            sink);
        for (int index = 0; index < settings.Iterations; index++)
        {
            RecognitionResult result = await recognizer.RecognizeAsync(strokes);
            RequireRecognitionResult(result);
        }

        LocalMetricsSnapshot snapshot = sink.Snapshot();
        Require(snapshot.SampleCount == expectedSamples, "recognition_sample_total");
        RequireCompleted(snapshot, MetricOperation.RecognitionProcessing, settings.Iterations);
        RequireCompleted(snapshot, MetricOperation.RecognitionPartition, settings.Iterations);
        RequireCompleted(snapshot, MetricOperation.RecognitionClassification, settings.Iterations);
        RequireCompleted(snapshot, MetricOperation.RecognitionGrammar, settings.Iterations);
        RequireItemCounts(snapshot, MetricOperation.RecognitionProcessing, 1);
        RequireItemCounts(snapshot, MetricOperation.RecognitionPartition, 1);
        RequireItemCounts(snapshot, MetricOperation.RecognitionClassification, 2);
        RequireItemCounts(snapshot, MetricOperation.RecognitionGrammar, 2);
        return snapshot;
    }

    private static void RequireRecognitionResult(RecognitionResult result) => Require(
        result.Latex == "++"
        && result.Tokens.Count == 2
        && result.Tokens.All(token => token.Latex == "+"),
        "recognition_correctness");

    private static LocalMetricsSnapshot RunSheetScenario(HarnessSettings settings)
    {
        RunSheetIterations(
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            settings.WarmupIterations);

        var sink = new BoundedInMemoryMetricsSink(checked(settings.Iterations + 16));
        var graph = new SheetGraph(
            new AngouriMathEvaluator(),
            new AngouriMathExpressionAnalyzer(),
            sink);
        RunSheetIterations(graph, settings.Iterations);

        LocalMetricsSnapshot snapshot = sink.Snapshot();
        Require(snapshot.SampleCount == settings.Iterations, "sheet_sample_total");
        RequireCompleted(snapshot, MetricOperation.SheetRecompute, settings.Iterations);
        RequireItemCounts(snapshot, MetricOperation.SheetRecompute, 3);
        return snapshot;
    }

    private static void RunSheetIterations(SheetGraph graph, int iterations)
    {
        Guid xId = SyntheticId(100);
        Guid yId = SyntheticId(101);
        Guid queryId = SyntheticId(102);
        graph.Upsert(xId, "x=1");
        graph.Upsert(yId, "y=x+2");
        SheetNode query = graph.Upsert(queryId, "y+1=");

        for (int index = 0; index < iterations; index++)
        {
            bool even = index % 2 == 0;
            graph.Upsert(xId, even ? "x=2" : "x=1");
            RecomputeReport report = graph.RecomputeDetailed();

            Require(
                report.CausallyAffectedNodes.Select(node => node.Id)
                    .SequenceEqual(new[] { xId, yId, queryId }),
                "sheet_causal_chain");
            Require(
                report.CausallyAffectedNodes.All(node => node.Result?.IsComputed == true),
                "sheet_computed_chain");
            Require(query.Result?.DisplayText == (even ? "5" : "4"), "sheet_correctness");
        }
    }

    private static async Task<LocalMetricsSnapshot> RunPersistenceScenarioAsync(
        HarnessSettings settings)
    {
        using (var warmupDirectory = new TemporaryDirectory("penumbra-metrics-persistence-warmup"))
        {
            await RunPersistenceIterationsAsync(
                settings.WarmupIterations,
                warmupDirectory.Path,
                NoOpLocalMetricsSink.Instance);
        }

        int expectedSamples = checked(settings.Iterations * 5);
        var sink = new BoundedInMemoryMetricsSink(checked(expectedSamples + 16));
        using var directory = new TemporaryDirectory("penumbra-metrics-persistence");
        await RunPersistenceIterationsAsync(settings.Iterations, directory.Path, sink);

        LocalMetricsSnapshot snapshot = sink.Snapshot();
        Require(snapshot.SampleCount == expectedSamples, "persistence_sample_total");
        RequireCompleted(snapshot, MetricOperation.ExplicitSave, settings.Iterations);
        RequireCompleted(snapshot, MetricOperation.Autosave, checked(settings.Iterations * 2));
        RequireCompleted(snapshot, MetricOperation.RecoveryRead, settings.Iterations);
        RequireCompleted(snapshot, MetricOperation.CloseFlush, settings.Iterations);
        RequireNoItemCounts(snapshot, MetricOperation.ExplicitSave);
        RequireNoItemCounts(snapshot, MetricOperation.Autosave);
        RequireNoItemCounts(snapshot, MetricOperation.RecoveryRead);
        RequireNoItemCounts(snapshot, MetricOperation.CloseFlush);
        return snapshot;
    }

    private static async Task RunPersistenceIterationsAsync(
        int iterations,
        string directory,
        ILocalMetricsSink metrics)
    {
        string explicitPath = Path.Combine(directory, "explicit.pen");
        string autosavePath = Path.Combine(directory, "autosave.pen");
        string closeFlushPath = Path.Combine(directory, "close-flush.pen");
        string recoveryPath = Path.Combine(directory, "recovery.pen");
        const string corruptCurrent = "synthetic-corrupt-current";
        const string recoveryMarker = "synthetic-recovery-backup";

        var seedStore = new FileSystemPageStore();
        PenumbraDocument recoveryBackup = CreatePersistenceDocument(recoveryMarker);
        PageSaveResult seededBackup = await seedStore.SaveAsync(
            recoveryBackup,
            recoveryPath,
            generation: 1,
            PageSaveKind.Explicit);
        Require(seededBackup.Status == PageSaveStatus.Committed, "persistence_recovery_seed_backup");
        PageSaveResult seededCurrent = await seedStore.SaveAsync(
            CreatePersistenceDocument("synthetic-recovery-current"),
            recoveryPath,
            generation: 2,
            PageSaveKind.Explicit);
        Require(seededCurrent.Status == PageSaveStatus.Committed, "persistence_recovery_seed_current");
        await File.WriteAllTextAsync(recoveryPath, corruptCurrent);

        var store = new FileSystemPageStore(metrics);
        await using var backgroundAutosave = new PageAutosaveCoordinator(
            store,
            TimeSpan.FromMilliseconds(1),
            metricsSink: metrics);
        await using var closeFlush = new PageAutosaveCoordinator(
            store,
            TimeSpan.FromMinutes(1),
            metricsSink: metrics);

        for (int index = 0; index < iterations; index++)
        {
            string marker = index.ToString(CultureInfo.InvariantCulture);
            PenumbraDocument document = CreatePersistenceDocument(marker);
            PageSaveResult explicitSave = await store.SaveAsync(
                document,
                explicitPath,
                index + 1,
                PageSaveKind.Explicit);
            Require(explicitSave.Status == PageSaveStatus.Committed, "persistence_explicit_commit");

            long backgroundRevision = await ScheduleAndWaitForBackgroundCommitAsync(
                backgroundAutosave,
                document,
                autosavePath);
            Require(
                backgroundAutosave.CommittedRevision == backgroundRevision,
                "persistence_background_autosave_commit");

            PenumbraDocument closeSnapshot = CreatePersistenceDocument($"{marker}-close");
            long closeRevision = closeFlush.Schedule(closeSnapshot, closeFlushPath);
            await closeFlush.FlushAsync();
            Require(
                closeFlush.CommittedRevision == closeRevision,
                "persistence_close_flush_commit");

            PageOpenResult recovered = await store.OpenAsync(recoveryPath);
            Require(
                recovered.Status == PageOpenStatus.BackupRecoveryCandidate,
                "persistence_recovery_source");
            Require(
                recovered.Document?.Variables.TryGetValue("synthetic_marker", out string? recoveredMarker)
                    == true
                && recoveredMarker == recoveryMarker,
                "persistence_recovery_correctness");
            Require(
                recovered.Document?.StrokeMetadata?.Count == recoveryBackup.Strokes.Count,
                "persistence_provenance_count");
            Require(
                await File.ReadAllTextAsync(recoveryPath) == corruptCurrent,
                "persistence_recovery_no_promotion");
        }
    }

    private static async Task<long> ScheduleAndWaitForBackgroundCommitAsync(
        PageAutosaveCoordinator coordinator,
        PenumbraDocument document,
        string path)
    {
        long expectedRevision = checked(coordinator.LatestRevision + 1);
        var terminalState = new TaskCompletionSource<PageAutosaveStateChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(object? _, PageAutosaveStateChangedEventArgs state)
        {
            if (state.Revision == expectedRevision)
            {
                terminalState.TrySetResult(state);
            }
        }

        coordinator.StateChanged += OnStateChanged;
        try
        {
            long revision = coordinator.Schedule(document, path);
            Require(revision == expectedRevision, "persistence_background_autosave_revision");
            if (coordinator.CommittedRevision >= revision)
            {
                return revision;
            }

            PageAutosaveStateChangedEventArgs state = await terminalState.Task.WaitAsync(
                TimeSpan.FromSeconds(10));
            Require(state.Committed, "persistence_background_autosave_terminal_state");
            Require(state.Failure is null, "persistence_background_autosave_failure");
            return revision;
        }
        finally
        {
            coordinator.StateChanged -= OnStateChanged;
        }
    }

    private static PenumbraDocument CreatePersistenceDocument(string marker)
    {
        var ink = new InkDocument();
        Stroke[] strokes = Enumerable.Range(0, 32)
            .Select(strokeIndex =>
            {
                double lineY = 20 + strokeIndex / 4 * 48;
                double startX = 20 + strokeIndex % 4 * 72;
                StrokeSample[] samples = Enumerable.Range(0, 12)
                    .Select(sampleIndex => new StrokeSample(
                        startX + sampleIndex * 3,
                        lineY + Math.Sin(sampleIndex * 0.6) * 12,
                        TimeSpan.FromMilliseconds(sampleIndex * 8),
                        0.45 + sampleIndex % 3 * 0.1))
                    .ToArray();
                return new Stroke(SyntheticId(400 + strokeIndex), samples);
            })
            .ToArray();
        ink.AddStrokes(strokes);
        return ink.ToDocument() with
        {
            Variables = new Dictionary<string, string> { ["synthetic_marker"] = marker },
            RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
        };
    }

    private static async Task<LocalMetricsSnapshot> RunTaffyScenarioAsync(
        HarnessSettings settings)
    {
        await WarmTaffyAndRefuteRejitterAsync(settings.WarmupIterations);

        TaffyFixture fixture = CreateTaffyFixture();
        int expectedSamples = checked(settings.Iterations * 5 + 3);
        var sink = new BoundedInMemoryMetricsSink(checked(expectedSamples + 16));
        var time = new AdvancingUtcTimeProvider();
        var graph = new SheetGraph(
            new AngouriMathEvaluator(),
            new AngouriMathExpressionAnalyzer(),
            sink,
            time);
        using var viewModel = CreateTaffyViewModel(fixture, graph, time, sink);
        LiteralRun run = await InitializeTaffyAsync(viewModel, fixture);

        Require(viewModel.BeginTaffy(fixture.OwnerId, run), "taffy_begin");
        for (int index = 0; index < settings.Iterations; index++)
        {
            time.AdvanceUtc(TaffyThrottleAdvance);
            viewModel.UpdateTaffy(screenDx: checked((index + 1) * 14));

            TaffyGhostLayer layer = viewModel.TaffyGhostLayer
                ?? throw new HarnessInvariantException("taffy_layer_presence");
            Require(layer.Ghosts.Count == 2, "taffy_ghost_count");
            Require(layer.Ghosts.Count(ghost => ghost.IsLiteral) == 1, "taffy_literal_ghost");
            Require(layer.Ghosts.Count(ghost => !ghost.IsLiteral) == 1, "taffy_query_ghost");
            Require(
                layer.Ghosts.Single(ghost => ghost.IsLiteral).ValueText
                    == (index + 6).ToString(CultureInfo.InvariantCulture),
                "taffy_literal_correctness");
            Require(
                layer.Ghosts.Single(ghost => !ghost.IsLiteral).ValueText
                    == (index + 7).ToString(CultureInfo.InvariantCulture),
                "taffy_query_correctness");
        }

        LocalMetricsSnapshot snapshot = sink.Snapshot();
        Require(snapshot.SampleCount == expectedSamples, "taffy_sample_total");
        RequireCompleted(snapshot, MetricOperation.SheetRecompute, 1);
        RequireCompleted(snapshot, MetricOperation.TaffyProcessing, settings.Iterations);
        RequireCompleted(snapshot, MetricOperation.TaffyProbe, settings.Iterations);
        RequireCompleted(
            snapshot,
            MetricOperation.TaffyGhostSynthesis,
            checked(settings.Iterations * 2 + 1));
        RequireCompleted(
            snapshot,
            MetricOperation.TaffyPublication,
            checked(settings.Iterations + 1));
        RequireItemCounts(snapshot, MetricOperation.SheetRecompute, 2);
        RequireItemCounts(snapshot, MetricOperation.TaffyProcessing, 2);
        RequireItemCounts(snapshot, MetricOperation.TaffyProbe, 2);
        Require(
            ObservationsFor(snapshot, MetricOperation.TaffyGhostSynthesis)
                .All(observation => observation.ItemCount > 0),
            "taffy_synthesis_item_count");
        MetricObservation[] publication = ObservationsFor(
            snapshot,
            MetricOperation.TaffyPublication);
        Require(publication[0].ItemCount == 1, "taffy_initial_publication_count");
        Require(
            publication.Skip(1).All(observation => observation.ItemCount == 2),
            "taffy_update_publication_count");
        return snapshot;
    }

    private static async Task WarmTaffyAndRefuteRejitterAsync(int warmupIterations)
    {
        TaffyFixture fixture = CreateTaffyFixture();
        var time = new AdvancingUtcTimeProvider();
        var graph = new SheetGraph(
            new AngouriMathEvaluator(),
            new AngouriMathExpressionAnalyzer(),
            NoOpLocalMetricsSink.Instance,
            time);
        using var viewModel = CreateTaffyViewModel(
            fixture,
            graph,
            time,
            NoOpLocalMetricsSink.Instance);
        LiteralRun run = await InitializeTaffyAsync(viewModel, fixture);

        Require(viewModel.BeginTaffy(fixture.OwnerId, run), "taffy_warmup_begin");
        for (int index = 0; index < warmupIterations; index++)
        {
            time.AdvanceUtc(TaffyThrottleAdvance);
            viewModel.UpdateTaffy(screenDx: checked((index + 1) * 14));
        }

        time.AdvanceUtc(TaffyThrottleAdvance);
        viewModel.UpdateTaffy(screenDx: 28);
        SynthesizedHandwriting? first = viewModel.TaffyGhostLayer?.Ghosts
            .SingleOrDefault(ghost => ghost.IsLiteral)?.Handwriting;
        Require(first is not null, "taffy_rejitter_first");

        time.AdvanceUtc(TaffyThrottleAdvance);
        viewModel.UpdateTaffy(screenDx: 14);
        time.AdvanceUtc(TaffyThrottleAdvance);
        viewModel.UpdateTaffy(screenDx: 28);
        SynthesizedHandwriting? repeated = viewModel.TaffyGhostLayer?.Ghosts
            .SingleOrDefault(ghost => ghost.IsLiteral)?.Handwriting;
        Require(ReferenceEquals(first, repeated), "taffy_rejitter_reference");
    }

    private static MainWindowViewModel CreateTaffyViewModel(
        TaffyFixture fixture,
        SheetGraph graph,
        TimeProvider time,
        ILocalMetricsSink metrics) => new(
        new StaticRegionRecognizer(fixture.Regions),
        graph,
        glyphBank: null,
        synthesizer: new HandwritingSynthesizer(new[] { new DeterministicGlyphSource() }),
        calibration: RecognitionCalibration.Default,
        time,
        metrics);

    private static async Task<LiteralRun> InitializeTaffyAsync(
        MainWindowViewModel viewModel,
        TaffyFixture fixture)
    {
        // This assignment deliberately precedes the first fixture stroke, preventing debounce/quiet-period
        // samples and keeping the scenario headless and exact-counted.
        viewModel.LiveRecognition = false;
        foreach (Stroke stroke in fixture.Regions
                     .SelectMany(region => region.Region.Groups)
                     .SelectMany(group => group.Strokes))
        {
            viewModel.Document.AddStroke(stroke);
        }

        await viewModel.RecognizeNowAsync();
        return viewModel.LiteralRunLayer.Owners
            .Single(owner => owner.OwnerId == fixture.OwnerId)
            .Runs.Single(candidate => candidate.ValueText == "5");
    }

    private static TaffyFixture CreateTaffyFixture()
    {
        RegionRecognition definition = CreateExpressionRegion(
            SyntheticId(200),
            strokeSeed: 210,
            latex: "x=5",
            y: 0,
            "x", "=", "5");
        RegionRecognition query = CreateExpressionRegion(
            SyntheticId(201),
            strokeSeed: 220,
            latex: "x+1=",
            y: 80,
            "x", "+", "1", "=");
        return new TaffyFixture(definition.Region.Id, new[] { definition, query });
    }

    private static RegionRecognition CreateExpressionRegion(
        Guid ownerId,
        int strokeSeed,
        string latex,
        double y,
        params string[] labels)
    {
        var strokes = new List<Stroke>(labels.Length);
        var tokens = new List<RecognizedToken>(labels.Length);
        for (int index = 0; index < labels.Length; index++)
        {
            Guid strokeId = SyntheticId(strokeSeed + index);
            double x = 10 + index * 28;
            var stroke = new Stroke(strokeId, new[]
            {
                new StrokeSample(x, y + 10, TimeSpan.Zero, 0.5),
                new StrokeSample(x + 16, y + 40, TimeSpan.FromMilliseconds(20), 0.5),
            });
            var bounds = new InkBounds(x, y + 10, 16, 30);
            strokes.Add(stroke);
            tokens.Add(new RecognizedToken(labels[index], new[] { strokeId }, bounds, 0.99));
        }

        var regionBounds = new InkBounds(
            10,
            y + 10,
            Math.Max(16, labels.Length * 28 - 12),
            30);
        StrokeGroup[] groups = strokes
            .Zip(tokens, (stroke, token) => new StrokeGroup(new[] { stroke }, token.Bounds))
            .ToArray();
        var region = new InkRegion(
            ownerId,
            strokes.Select(stroke => stroke.Id).ToArray(),
            regionBounds,
            groups);
        var result = new RecognitionResult(latex, tokens, 0.99, 0.99);
        return new RegionRecognition(region, result, Dirty: true);
    }

    private static IReadOnlyList<Stroke> CreateRecognitionLine() => new[]
    {
        CreateLine(1, (0, 20), (5, 20), (10, 20), (15, 20), (20, 20)),
        CreateLine(2, (10, 10), (10, 15), (10, 20), (10, 25), (10, 30)),
        CreateLine(3, (60, 20), (65, 20), (70, 20), (75, 20), (80, 20)),
        CreateLine(4, (70, 10), (70, 15), (70, 20), (70, 25), (70, 30)),
    };

    private static Stroke CreateLine(int seed, params (double X, double Y)[] points) => new(
        SyntheticId(seed),
        points.Select((point, index) => new StrokeSample(
            point.X,
            point.Y,
            TimeSpan.FromMilliseconds(index * 5),
            0.5)).ToArray());

    private static Guid SyntheticId(int seed)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static void RequireCompleted(
        LocalMetricsSnapshot snapshot,
        MetricOperation operation,
        int expectedCount)
    {
        MetricOperationSummary summary = snapshot.SummaryFor(operation);
        Require(summary.SampleCount == expectedCount, $"{operation}_retained_count");
        Require(summary.CompletedCount == expectedCount, $"{operation}_completed_count");
        Require(summary.CancelledCount == 0, $"{operation}_cancelled_count");
        Require(summary.RefusedCount == 0, $"{operation}_refused_count");
        Require(summary.FailedCount == 0, $"{operation}_failed_count");
        Require(summary.CompletedDurationP50 is not null, $"{operation}_p50_missing");
        Require(summary.CompletedDurationP95 is not null, $"{operation}_p95_missing");
    }

    private static void RequireItemCounts(
        LocalMetricsSnapshot snapshot,
        MetricOperation operation,
        int expectedItemCount) => Require(
        ObservationsFor(snapshot, operation)
            .All(observation => observation.ItemCount == expectedItemCount),
        $"{operation}_item_count");

    private static void RequireNoItemCounts(
        LocalMetricsSnapshot snapshot,
        MetricOperation operation) => Require(
        ObservationsFor(snapshot, operation)
            .All(observation => observation.ItemCount is null),
        $"{operation}_private_item_count");

    private static MetricObservation[] ObservationsFor(
        LocalMetricsSnapshot snapshot,
        MetricOperation operation) => snapshot.Observations
        .Where(observation => observation.Operation == operation)
        .ToArray();

    private static int PrintRow(
        string scenario,
        LocalMetricsSnapshot snapshot,
        MetricOperation operation,
        double? candidateBudgetMilliseconds = null)
    {
        MetricOperationSummary summary = snapshot.SummaryFor(operation);
        bool warning = candidateBudgetMilliseconds is double budget
            && summary.CompletedDurationP95!.Value.TotalMilliseconds > budget;
        string budgetText = candidateBudgetMilliseconds?.ToString(
            "F0",
            CultureInfo.InvariantCulture) ?? "NA";
        Console.WriteLine(
            $"scenario={scenario} operation={operation} retained={summary.SampleCount} "
            + $"completed={summary.CompletedCount} cancelled={summary.CancelledCount} "
            + $"refused={summary.RefusedCount} failed={summary.FailedCount} "
            + $"completed_p50_ms={FormatMilliseconds(summary.CompletedDurationP50)} "
            + $"completed_p95_ms={FormatMilliseconds(summary.CompletedDurationP95)} "
            + $"candidate_budget_ms={budgetText} status={(warning ? "WARN" : "PASS")}");
        return warning ? 1 : 0;
    }

    private static string FormatMilliseconds(TimeSpan? duration) => duration?.TotalMilliseconds.ToString(
        "F3",
        CultureInfo.InvariantCulture) ?? "NA";

    private static void Require(bool condition, string code)
    {
        if (!condition)
        {
            throw new HarnessInvariantException(code);
        }
    }

    private readonly record struct HarnessSettings(
        int Iterations,
        int WarmupIterations,
        bool ShowHelp)
    {
        public static bool TryParse(string[] args, out HarnessSettings settings)
        {
            int iterations = DefaultIterations;
            int warmupIterations = DefaultWarmupIterations;
            bool showHelp = false;

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (argument is "--help" or "-h")
                {
                    showHelp = true;
                    continue;
                }

                if (argument is not ("--iterations" or "--warmup")
                    || index + 1 >= args.Length
                    || !int.TryParse(
                        args[++index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int value))
                {
                    settings = default;
                    return false;
                }

                if (argument == "--iterations")
                {
                    iterations = value;
                }
                else
                {
                    warmupIterations = value;
                }
            }

            if (iterations is < 1 or > MaximumIterations
                || warmupIterations is < 1 or > MaximumWarmupIterations)
            {
                settings = default;
                return false;
            }

            settings = new HarnessSettings(iterations, warmupIterations, showHelp);
            return true;
        }
    }

    private sealed class StaticRegionRecognizer(IReadOnlyList<RegionRecognition> regions)
        : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return regions;
        }

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Task.FromResult(
            RecognizeRegions(strokes, previous, cancellationToken));
    }

    private sealed class DeterministicGlyphSource : IGlyphSource
    {
        private static readonly IReadOnlyList<Stroke> Glyph = new[]
        {
            new Stroke(SyntheticId(300), new[]
            {
                new StrokeSample(0.2, 0.1, TimeSpan.Zero, 0.5),
                new StrokeSample(0.5, 0.5, TimeSpan.FromMilliseconds(10), 0.5),
                new StrokeSample(0.8, 0.9, TimeSpan.FromMilliseconds(20), 0.5),
            }),
        };

        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) => Glyph;
    }

    /// <summary>
    /// Advances only wall-clock UTC for the taffy throttle. Metric scopes still use the system's real
    /// monotonic timestamp and frequency, so advancing a trial never manufactures latency.
    /// </summary>
    private sealed class AdvancingUtcTimeProvider : TimeProvider
    {
        private long _utcOffsetTicks;

        public override DateTimeOffset GetUtcNow() =>
            TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _utcOffsetTicks));

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) => TimeProvider.System.CreateTimer(callback, state, dueTime, period);

        public void AdvanceUtc(TimeSpan duration) =>
            Interlocked.Add(ref _utcOffsetTicks, duration.Ticks);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception error) when (
                    attempt < 4 && error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20);
                }
            }
        }
    }

    private sealed class HarnessInvariantException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private sealed record TaffyFixture(
        Guid OwnerId,
        IReadOnlyList<RegionRecognition> Regions);
}
