using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.ExpressionCorpus;
using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class ProductExpressionScenarioRuntimeTests
{
    [Fact]
    public async Task Cli_RunUsesShippedProductIdentityAndRejectsAnEmptyCorpusAsEvidence()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "penumbra-product-runtime-cli-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "development"));
        Directory.CreateDirectory(Path.Combine(root, "held-out"));
        try
        {
            var manifest = new CorpusManifestV1(
                CorpusFormatV1.ManifestFormat,
                CorpusFormatV1.SchemaVersion,
                "phase-5.5-v1",
                []);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "manifest.v1.json"),
                CorpusJson.SerializeToUtf8Bytes(manifest));
            using var output = new StringWriter();

            int exitCode = await CorpusCli.RunAsync(
                ["run", "--root", root, "--partition", "development"],
                output);
            string text = output.ToString();

            Assert.Equal(1, exitCode);
            Assert.Contains("\"validatedCaseCount\": 0", text, StringComparison.Ordinal);
            Assert.Contains("\"modelFingerprint\":", text, StringComparison.Ordinal);
            Assert.Contains("overall=FAIL", text, StringComparison.Ordinal);
            Assert.DoesNotContain("headless_product_runtime_unavailable", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_ExecutesSyntheticFixtureThroughRecognitionCasSheetAndSharedPageTransaction()
    {
        ExpressionCaseV1 @case = QueryWithTaffyCase();
        Assert.Empty(CorpusValidator.ValidateCase(@case));
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('b', 64),
            synthesizer: new HandwritingSynthesizer([new AnyGlyphSource()]));

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            factory,
            new CorpusRunOptions(CorpusPartitionV1.Development),
            default);

        Assert.True(report.InfrastructureValid, CorpusReportJson.Serialize(report));
        Assert.True(report.ProfilePassed, CorpusReportJson.Serialize(report));
        Assert.Equal(1, report.ValidatedCaseCount);
        Assert.Equal(1, report.ExecutedCaseCount);
        Assert.Equal(1, report.ExactExpressionNumerator);
        Assert.Equal(1, report.ExactExpressionDenominator);
        Assert.Equal(0, report.AcceptedWrongCount);
        Assert.Equal(0, report.StructuralMismatchCount);
        Assert.Equal(new string('b', 64), report.ModelFingerprint);
        Assert.Equal(0, report.MissingMetricOperationCount);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.RecognitionClassification.ToString()
            && metric.CompletedCount == 1);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.SheetRecompute.ToString()
            && metric.CompletedCount == 1);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.TaffyProcessing.ToString()
            && metric.CompletedCount == 1);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.TaffyProbe.ToString()
            && metric.CompletedCount == 1);
    }

    [Fact]
    public async Task Runtime_UsesInkDocumentHistoryAndReportsOriginSetsWithoutOracleData()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            ["stroke-1"]);
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('c', 64));
        await using IExpressionScenarioRuntime runtime = factory.Create(
            input,
            NoOpLocalMetricsSink.Instance);

        MutationActualV1 erased = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
            new EraseActionV1(["stroke-1"]),
            default));
        MutationActualV1 restored = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
            new UndoActionV1(),
            default));
        MutationActualV1 added = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
            new AddInkActionV1(["stroke-plus"]),
            default));
        MutationActualV1 redoneAfterBranch = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
            new RedoActionV1(),
            default));

        Assert.Empty(erased.State.LiveStrokeIds);
        Assert.Equal(["stroke-1"], restored.State.LiveStrokeIds);
        Assert.Equal(["stroke-1", "stroke-plus"], added.State.LiveStrokeIds);
        Assert.Equal(added.State.LiveStrokeIds, redoneAfterBranch.State.LiveStrokeIds);
        Assert.Equal(added.State.UserInkStrokeIds, redoneAfterBranch.State.UserInkStrokeIds);
        Assert.Equal(added.State.SynthesizedStrokeIds, redoneAfterBranch.State.SynthesizedStrokeIds);
        Assert.Equal(added.State.LiveStrokeIds.Order(), added.State.UserInkStrokeIds.Order());
        Assert.Empty(added.State.SynthesizedStrokeIds);
    }

    [Fact]
    public async Task Runtime_StampExecutesSharedAnswerAndInkTransactionWithFreshProvenance()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('d', 64),
            synthesizer: new HandwritingSynthesizer([new AnyGlyphSource()]));
        await using IExpressionScenarioRuntime runtime = factory.Create(
            input,
            NoOpLocalMetricsSink.Instance);

        RecognizeActualV1 recognized = Assert.IsType<RecognizeActualV1>(
            await runtime.ApplyAsync(new RecognizeActionV1(), default));
        string owner = recognized.Actual.Regions.Single().RuntimeRegionHandle;
        StampActualV1 stamp = Assert.IsType<StampActualV1>(await runtime.ApplyAsync(
            new StampActionV1(
                owner,
                new CorpusPointV1(10, 100),
                new CorpusPointV1(150, 200)),
            default));

        Assert.Equal(CorpusStampDecisionV1.Append, stamp.Decision);
        Assert.Equal(1, stamp.AppliedScale);
        CorpusStrokeV1 source = Assert.Single(stamp.SourceStrokes);
        CorpusStrokeV1 added = Assert.Single(stamp.AddedStrokes);
        Assert.NotEqual(source.StrokeId, added.StrokeId);
        Assert.Empty(stamp.RemovedStrokeIds);
        Assert.Equal([added.StrokeId], stamp.State.SynthesizedStrokeIds);
        Assert.Equal(source.Samples.Count, added.Samples.Count);
        for (int index = 0; index < source.Samples.Count; index++)
        {
            Assert.Equal(source.Samples[index].X + 10, added.Samples[index].X, 6);
            Assert.Equal(source.Samples[index].Y + 100, added.Samples[index].Y, 6);
            Assert.Equal(source.Samples[index].ElapsedTicks, added.Samples[index].ElapsedTicks);
            Assert.Equal(source.Samples[index].Pressure, added.Samples[index].Pressure);
        }

        MutationActualV1 erased = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
            new EraseActionV1([added.StrokeId]),
            default));
        Assert.Empty(erased.State.SynthesizedStrokeIds);

        MutationActualV1 restored = Assert.IsType<MutationActualV1>(
            await runtime.ApplyAsync(new UndoActionV1(), default));
        Assert.Equal([added.StrokeId], restored.State.SynthesizedStrokeIds);

        MutationActualV1 undone = Assert.IsType<MutationActualV1>(
            await runtime.ApplyAsync(new UndoActionV1(), default));
        Assert.Empty(undone.State.SynthesizedStrokeIds);
        Assert.Equal(@case.InitialStrokeIds.Order(), undone.State.LiveStrokeIds.Order());
    }

    [Fact]
    public async Task Runtime_TaffyHitTestsProbesPublishesMetricsAndLeavesCommittedPageUnchanged()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        var metrics = new BoundedInMemoryMetricsSink(32);
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('7', 64),
            synthesizer: new HandwritingSynthesizer([new AnyGlyphSource()]));
        await using IExpressionScenarioRuntime runtime = factory.Create(input, metrics);

        RecognizeActualV1 before = Assert.IsType<RecognizeActualV1>(
            await runtime.ApplyAsync(new RecognizeActionV1(), default));
        ActualRegionV1 region = Assert.Single(before.Actual.Regions);
        TaffyProbeActualV1 probe = Assert.IsType<TaffyProbeActualV1>(await runtime.ApplyAsync(
            new TaffyProbeActionV1(
                new CorpusPointV1(5, 5),
                CumulativeScreenDeltaX: 14,
                CanvasScale: 1),
            default));

        Assert.Equal("2+1=", probe.TrialLatex);
        ActualSheetNodeV1 trialNode = Assert.Single(probe.Sheet.Nodes);
        Assert.Equal(region.RuntimeRegionHandle, trialNode.RuntimeRegionHandle);
        Assert.Equal(new ExpectedEvaluationV1(CorpusEvaluationKindV1.Number, true, "3"), trialNode.Result);
        Assert.Equal([region.RuntimeRegionHandle], probe.Sheet.ChangedRegionHandles);
        Assert.Equal([region.RuntimeRegionHandle], probe.Sheet.CausallyAffectedRegionHandles);

        RecognizeActualV1 after = Assert.IsType<RecognizeActualV1>(
            await runtime.ApplyAsync(new RecognizeActionV1(), default));
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(
            Assert.Single(after.Actual.Regions).Outcome);
        Assert.Equal("1+1=", accepted.Latex);
        Assert.Equal(new ExpectedEvaluationV1(CorpusEvaluationKindV1.Number, true, "2"), accepted.Cas);
        Assert.Equal(
            before.Actual.Regions.Single().StrokeIds,
            after.Actual.Regions.Single().StrokeIds);

        LocalMetricsSnapshot snapshot = metrics.Snapshot();
        Assert.Contains(snapshot.Observations, observation =>
            observation.Operation == MetricOperation.TaffyProcessing
            && observation.Outcome == MetricOutcome.Completed);
        Assert.Contains(snapshot.Observations, observation =>
            observation.Operation == MetricOperation.TaffyProbe
            && observation.Outcome == MetricOutcome.Completed);
        Assert.Contains(snapshot.Observations, observation =>
            observation.Operation == MetricOperation.TaffyGhostSynthesis
            && observation.Outcome == MetricOutcome.Completed);
        Assert.Contains(snapshot.Observations, observation =>
            observation.Operation == MetricOperation.TaffyPublication
            && observation.Outcome == MetricOutcome.Completed);
    }

    [Fact]
    public async Task Runtime_SaveReopenPreservesFreshStampAliasOriginAndCleansItsIsolatedStore()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('8', 64),
            synthesizer: new HandwritingSynthesizer([new AnyGlyphSource()]));
        var runtime = Assert.IsType<ProductExpressionScenarioRuntime>(factory.Create(
            input,
            NoOpLocalMetricsSink.Instance));
        string storageRoot = runtime.StorageRootPath;
        try
        {
            RecognizeActualV1 recognized = Assert.IsType<RecognizeActualV1>(
                await runtime.ApplyAsync(new RecognizeActionV1(), default));
            StampActualV1 stamped = Assert.IsType<StampActualV1>(await runtime.ApplyAsync(
                new StampActionV1(
                    recognized.Actual.Regions.Single().RuntimeRegionHandle,
                    new CorpusPointV1(10, 100),
                    new CorpusPointV1(150, 200)),
                default));
            string stampAlias = Assert.Single(stamped.AddedStrokes).StrokeId;
            PersistenceWriteActualV1 saved = Assert.IsType<PersistenceWriteActualV1>(await runtime.ApplyAsync(
                new SaveActionV1("page", CorpusSaveModeV1.Explicit),
                default));
            Assert.True(saved.Completed);
            Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
                new EraseActionV1([stampAlias]),
                default));

            PersistenceOpenActualV1 reopened = Assert.IsType<PersistenceOpenActualV1>(
                await runtime.ApplyAsync(new ReopenActionV1("page"), default));

            Assert.Equal(CorpusOpenStatusV1.OpenedCurrent, reopened.Status);
            Assert.Contains(stampAlias, reopened.State.LiveStrokeIds);
            Assert.Contains(stampAlias, reopened.State.SynthesizedStrokeIds);
            MutationActualV1 erasedAfterReopen = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
                new EraseActionV1([stampAlias]),
                default));
            Assert.DoesNotContain(stampAlias, erasedAfterReopen.State.LiveStrokeIds);
            MutationActualV1 restored = Assert.IsType<MutationActualV1>(await runtime.ApplyAsync(
                new UndoActionV1(),
                default));
            Assert.Contains(stampAlias, restored.State.SynthesizedStrokeIds);
        }
        finally
        {
            await runtime.DisposeAsync();
        }

        Assert.False(Directory.Exists(storageRoot));
    }

    [Fact]
    public async Task Runtime_AutosaveWaitsForCoordinatorQuietPeriodAndCommitsItsRevision()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        var time = new FakeTimeProvider();
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('7', 64),
            timeProvider: time);
        var metrics = new BoundedInMemoryMetricsSink(16);
        var runtime = Assert.IsType<ProductExpressionScenarioRuntime>(factory.Create(
            input,
            metrics));
        try
        {
            Task<StepActualV1> pending = runtime.ApplyAsync(
                new SaveActionV1("page", CorpusSaveModeV1.Autosave),
                default);

            Assert.Equal(1, runtime.AutosaveLatestRevision);
            Assert.Equal(0, runtime.AutosaveCommittedRevision);
            Assert.False(pending.IsCompleted);
            time.Advance(ProductExpressionScenarioRuntime.AutosaveQuietPeriod - TimeSpan.FromMilliseconds(1));
            await Task.Yield();
            Assert.False(pending.IsCompleted);

            time.Advance(TimeSpan.FromMilliseconds(1));
            PersistenceWriteActualV1 saved = Assert.IsType<PersistenceWriteActualV1>(
                await pending.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(saved.Completed);
            Assert.Equal(1, runtime.AutosaveCommittedRevision);
            LocalMetricsSnapshot snapshot = metrics.Snapshot();
            Assert.Single(snapshot.Observations, observation =>
                observation.Operation == MetricOperation.Autosave
                && observation.Outcome == MetricOutcome.Completed);
            Assert.DoesNotContain(snapshot.Observations, observation =>
                observation.Operation == MetricOperation.CloseFlush);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_DisposeCancelsAnInFlightAutosaveBeforeDeletingItsOwnedRoot()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        var time = new FakeTimeProvider();
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('5', 64),
            timeProvider: time);
        var runtime = Assert.IsType<ProductExpressionScenarioRuntime>(factory.Create(
            input,
            NoOpLocalMetricsSink.Instance));
        string storageRoot = runtime.StorageRootPath;
        Task<StepActualV1> pending = runtime.ApplyAsync(
            new SaveActionV1("page", CorpusSaveModeV1.Autosave),
            default);
        Assert.False(pending.IsCompleted);

        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Directory.Exists(storageRoot));
    }

    [Fact]
    public async Task Runner_ExecutesRealSaveAutosaveCloseFlushAndRecoveryWithoutCacheAuthority()
    {
        ExpressionCaseV1 @case = QueryWithPersistenceCase();
        Assert.Empty(CorpusValidator.ValidateCase(@case));
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('9', 64),
            synthesizer: new HandwritingSynthesizer([new AnyGlyphSource()]));

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            factory,
            new CorpusRunOptions(CorpusPartitionV1.Development, MetricsCapacity: 256),
            default);

        Assert.True(report.InfrastructureValid, CorpusReportJson.Serialize(report));
        Assert.True(report.ProfilePassed, CorpusReportJson.Serialize(report));
        Assert.Equal(5, report.ExactExpressionNumerator);
        Assert.Equal(5, report.ExactExpressionDenominator);
        Assert.Equal(0, report.StructuralMismatchCount);
        Assert.Equal(0, report.Failures[CorpusFailureCategoryV1.Persistence]);
        Assert.Equal(0, report.MissingMetricOperationCount);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.ExplicitSave.ToString()
            && metric.CompletedCount == 1);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.Autosave.ToString()
            && metric.CompletedCount == 2);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.CloseFlush.ToString()
            && metric.CompletedCount == 1);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.RecoveryRead.ToString()
            && metric.CompletedCount == 4);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.RecognitionClassification.ToString()
            && metric.CompletedCount == 5);
        Assert.Contains(report.Metrics, metric =>
            metric.Operation == MetricOperation.RecognitionGrammar.ToString()
            && metric.CompletedCount == 5);
    }

    [Fact]
    public async Task Runner_EmptyPageRecoveryDoesNotDemandNonexistentClassifierOrGrammarWork()
    {
        ExpressionCaseV1 @case = EmptyPersistenceCase();
        Assert.Empty(CorpusValidator.ValidateCase(@case));
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('6', 64));

        CorpusRunReport report = await new ExpressionCorpusRunner().RunAsync(
            CorpusTestData.Suite((@case, CorpusCaseStatusV1.Development, null)),
            factory,
            new CorpusRunOptions(CorpusPartitionV1.Development, MetricsCapacity: 64),
            default);

        Assert.True(report.InfrastructureValid, CorpusReportJson.Serialize(report));
        Assert.Equal(0, report.MissingMetricOperationCount);
        Assert.DoesNotContain(report.Metrics, metric =>
            (metric.Operation is nameof(MetricOperation.RecognitionClassification)
                or nameof(MetricOperation.RecognitionGrammar))
            && metric.CompletedCount > 0);
    }

    [Fact]
    public async Task Runtime_ReportsExplicitUnavailableCapabilityInsteadOfFabricatingGraph()
    {
        ExpressionCaseV1 @case = QueryCase();
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new GeometryClassifier(),
            RecognitionCalibration.Default,
            new string('d', 64));
        await using IExpressionScenarioRuntime runtime = factory.Create(
            input,
            NoOpLocalMetricsSink.Instance);

        CapabilityUnavailableActualV1 graph = Assert.IsType<CapabilityUnavailableActualV1>(
            await runtime.ApplyAsync(
                new GraphActionV1("opaque-region", -1, 1, 10),
                default));

        Assert.Equal(CorpusCapabilityV1.Graph, graph.Capability);
    }

    [Fact]
    public async Task Runtime_MapsEnergyAndConfiguredConfidenceRefusalsWithoutFeedingSheet()
    {
        ExpressionCaseV1 @case = QueryCase() with
        {
            Strokes = [QueryCase().Strokes[0]],
            InitialStrokeIds = ["stroke-1"],
        };
        var input = new ExpressionScenarioInputV1(
            @case.Capture.Source,
            @case.Capture.DeviceClass,
            @case.Capture.PressureMode,
            @case.Strokes,
            @case.InitialStrokeIds);
        var calibration = RecognitionCalibration.Default with { MinConfidence = 0.8 };
        using var lowFactory = new ProductExpressionScenarioRuntimeFactory(
            new FixedClassifier(new SymbolPrediction("1", 0.7)),
            calibration,
            new string('e', 64));
        using var oodFactory = new ProductExpressionScenarioRuntimeFactory(
            new FixedClassifier(new SymbolPrediction("1", 0.99, Rejected: true)),
            calibration,
            new string('f', 64));

        RefusedRegionActualV1 low = await RecognizeRefusal(input, lowFactory);
        RefusedRegionActualV1 ood = await RecognizeRefusal(input, oodFactory);

        Assert.Equal(CorpusFailureCategoryV1.SymbolClassification, low.FirstStage);
        Assert.Equal(CorpusRefusalCodeV1.LowConfidence, low.Reason);
        Assert.Equal(CorpusFailureCategoryV1.SymbolClassification, ood.FirstStage);
        Assert.Equal(CorpusRefusalCodeV1.OutOfDistribution, ood.Reason);
    }

    [Fact]
    public async Task Runtime_MapsAStructuralGrammarRefusalToItsRealParseRefusalReasonNotAFixedGuess()
    {
        // Every symbol is confidently read ("(", "x", "+", "1" at 0.99) so RecognitionGate's confidence/OOD
        // gate is satisfied; the refusal actually reaching the corpus DTO can only be the spatial grammar's
        // own UnmatchedBracket verdict (real SpatialLayoutParser: an opening bracket with no closer), which
        // RefusedOutcome must now report honestly instead of the old fixed (Assembly, UnsupportedNotation).
        CorpusStrokeV1[] strokes =
        [
            Stroke("stroke-open", 0),
            Stroke("stroke-x", 30),
            Stroke("stroke-plus", 60),
            Stroke("stroke-1", 90),
        ];
        var input = new ExpressionScenarioInputV1(
            CorpusCaptureSourceV1.Synthetic,
            CorpusDeviceClassV1.Synthetic,
            CorpusPressureModeV1.Normalized,
            strokes,
            strokes.Select(stroke => stroke.StrokeId).ToArray());
        using var factory = new ProductExpressionScenarioRuntimeFactory(
            new UnmatchedOpenBracketClassifier(),
            RecognitionCalibration.Default,
            new string('c', 64));

        RefusedRegionActualV1 refusal = await RecognizeRefusal(input, factory);

        Assert.Equal(CorpusFailureCategoryV1.SpatialRelation, refusal.FirstStage);
        Assert.Equal(CorpusRefusalCodeV1.MalformedStructure, refusal.Reason);
    }

    [Fact]
    public void Runtime_MapsEveryCoreLayoutNodeRoleAndOwnedTokenByReference()
    {
        RecognizedToken[] tokens = Enumerable.Range(0, 22)
            .Select(index => new RecognizedToken(
                index.ToString(),
                [Guid.NewGuid()],
                new InkBounds(index, 0, 1, 1),
                1.0))
            .ToArray();
        var root = new SequenceNode(
        [
            new LeafNode(tokens[0]),
            new ImplicitProductNode([new LeafNode(tokens[1]), new LeafNode(tokens[2])]),
            new ScriptNode(new LeafNode(tokens[3]), new LeafNode(tokens[4]), new LeafNode(tokens[5])),
            new FractionNode(new LeafNode(tokens[6]), new LeafNode(tokens[7]), tokens[8]),
            new RadicalNode(new LeafNode(tokens[9]), new LeafNode(tokens[10]), tokens[11]),
            new DelimitedGroupNode(new LeafNode(tokens[12]), tokens[13], tokens[14]),
            new FunctionCallNode("sin", [tokens[15], tokens[16], tokens[17]], new LeafNode(tokens[18])),
            new RelationNode(new LeafNode(tokens[19]), tokens[20], new LeafNode(tokens[21])),
        ]);

        ActualLayoutNodeV1 actual = ProductExpressionScenarioRuntime.ActualLayout(root, tokens);

        Assert.Equal(LayoutKindV1.Sequence, actual.Kind);
        Assert.All(actual.Children, edge => Assert.Equal(LayoutRoleV1.Item, edge.Role));
        Assert.Equal(LayoutKindV1.Token, actual.Children[0].Node.Kind);
        Assert.Equal([0], actual.Children[0].Node.OwnedTokenIndexes);

        ActualLayoutNodeV1 product = actual.Children[1].Node;
        Assert.Equal(LayoutKindV1.ImplicitProduct, product.Kind);
        Assert.Equal([LayoutRoleV1.Factor, LayoutRoleV1.Factor], product.Children.Select(edge => edge.Role));

        ActualLayoutNodeV1 script = actual.Children[2].Node;
        Assert.Equal(LayoutKindV1.Script, script.Kind);
        Assert.Equal(
            [LayoutRoleV1.Base, LayoutRoleV1.Superscript, LayoutRoleV1.Subscript],
            script.Children.Select(edge => edge.Role));

        ActualLayoutNodeV1 fraction = actual.Children[3].Node;
        Assert.Equal(LayoutKindV1.Fraction, fraction.Kind);
        Assert.Equal([8], fraction.OwnedTokenIndexes);
        Assert.Equal(
            [LayoutRoleV1.Numerator, LayoutRoleV1.Denominator],
            fraction.Children.Select(edge => edge.Role));

        ActualLayoutNodeV1 radical = actual.Children[4].Node;
        Assert.Equal(LayoutKindV1.Radical, radical.Kind);
        Assert.Equal([11], radical.OwnedTokenIndexes);
        Assert.Equal(
            [LayoutRoleV1.Radicand, LayoutRoleV1.RootIndex],
            radical.Children.Select(edge => edge.Role));

        ActualLayoutNodeV1 group = actual.Children[5].Node;
        Assert.Equal(LayoutKindV1.DelimitedGroup, group.Kind);
        Assert.Equal([13, 14], group.OwnedTokenIndexes);
        Assert.Equal(LayoutRoleV1.Body, Assert.Single(group.Children).Role);

        ActualLayoutNodeV1 function = actual.Children[6].Node;
        Assert.Equal(LayoutKindV1.FunctionCall, function.Kind);
        Assert.Empty(function.OwnedTokenIndexes);
        Assert.Equal(
            [LayoutRoleV1.Function, LayoutRoleV1.Argument],
            function.Children.Select(edge => edge.Role));
        ActualLayoutNodeV1 functionName = function.Children[0].Node;
        Assert.Equal(LayoutKindV1.Sequence, functionName.Kind);
        Assert.Equal([15, 16, 17], functionName.Children.SelectMany(edge => edge.Node.OwnedTokenIndexes));

        ActualLayoutNodeV1 relation = actual.Children[7].Node;
        Assert.Equal(LayoutKindV1.Relation, relation.Kind);
        Assert.Equal([20], relation.OwnedTokenIndexes);
        Assert.Equal(
            [LayoutRoleV1.Left, LayoutRoleV1.Right],
            relation.Children.Select(edge => edge.Role));
    }

    [Fact]
    public void Runtime_RefusesAValueEqualButForeignLayoutTokenReference()
    {
        var resultToken = new RecognizedToken(
            "7", [Guid.NewGuid()], new InkBounds(0, 0, 1, 1), 1.0);
        var impostor = new RecognizedToken(
            resultToken.Latex,
            resultToken.SourceStrokeIds,
            resultToken.Bounds,
            resultToken.Confidence,
            resultToken.Rejected);
        Assert.Equal(resultToken, impostor);
        Assert.NotSame(resultToken, impostor);

        Assert.Throws<InvalidOperationException>(() =>
            ProductExpressionScenarioRuntime.ActualLayout(new LeafNode(impostor), [resultToken]));
    }

    [Fact]
    public void StructuralRefusalMapping_PinsEveryReasonAndCorpusSemantic()
    {
        var expected = new Dictionary<ParseRefusalReason, (CorpusFailureCategoryV1, CorpusRefusalCodeV1)>
        {
            [ParseRefusalReason.UnmatchedBracket] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.MalformedStructure),
            [ParseRefusalReason.UncertainScript] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
            [ParseRefusalReason.GeneralSubscript] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.UnsupportedNotation),
            [ParseRefusalReason.AmbiguousFractionOwnership] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
            [ParseRefusalReason.EmptyRadicalOwnership] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
            [ParseRefusalReason.AmbiguousFunctionWord] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
            [ParseRefusalReason.DigitProductAmbiguity] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
            [ParseRefusalReason.UnsupportedRelation] =
                (CorpusFailureCategoryV1.Assembly, CorpusRefusalCodeV1.UnsupportedNotation),
            [ParseRefusalReason.UnsupportedNotation] =
                (CorpusFailureCategoryV1.Assembly, CorpusRefusalCodeV1.UnsupportedNotation),
            [ParseRefusalReason.LostStroke] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.UnownedStroke),
            [ParseRefusalReason.DoubleOwnership] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.DuplicateStrokeOwnership),
            [ParseRefusalReason.LowMargin] =
                (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        };
        Assert.Equal(
            Enum.GetValues<ParseRefusalReason>().Where(reason => reason != ParseRefusalReason.None),
            expected.Keys.OrderBy(reason => reason));

        foreach ((ParseRefusalReason reason, var pair) in expected)
        {
            Assert.Equal(pair, ProductExpressionScenarioRuntime.StructuralRefusalCategory(reason));
            Assert.True(CorpusRefusalSemanticsV1.IsValid(pair.Item1, pair.Item2));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductExpressionScenarioRuntime.StructuralRefusalCategory(ParseRefusalReason.None));
    }

    private static async Task<RefusedRegionActualV1> RecognizeRefusal(
        ExpressionScenarioInputV1 input,
        ProductExpressionScenarioRuntimeFactory factory)
    {
        await using IExpressionScenarioRuntime runtime = factory.Create(
            input,
            NoOpLocalMetricsSink.Instance);
        RecognizeActualV1 actual = Assert.IsType<RecognizeActualV1>(
            await runtime.ApplyAsync(new RecognizeActionV1(), default));
        Assert.Empty(actual.Actual.Sheet!.Nodes);
        return Assert.IsType<RefusedRegionActualV1>(actual.Actual.Regions.Single().Outcome);
    }

    private static ExpressionCaseV1 QueryCase()
    {
        CorpusStrokeV1[] strokes =
        [
            Stroke("stroke-1", 0),
            Stroke("stroke-plus", 30),
            Stroke("stroke-2", 60),
            Stroke("stroke-equals", 90),
        ];
        ExpectedTokenV1[] tokens =
        [
            new("token-1", "1", ["stroke-1"]),
            new("token-plus", "+", ["stroke-plus"]),
            new("token-2", "1", ["stroke-2"]),
            new("token-equals", "=", ["stroke-equals"]),
        ];
        var evaluation = new ExpectedEvaluationV1(CorpusEvaluationKindV1.Number, true, "2");
        var page = new ExpectedPageV1(
        [
            new ExpectedRegionV1(
                "region-query",
                strokes.Select(stroke => stroke.StrokeId).ToArray(),
                new CorpusBoundsV1(0, 0, 100, 10),
                0.001,
                new AcceptedRegionExpectationV1(
                    "1+1=",
                    tokens,
                    new ExpectedLayoutNodeV1(
                        LayoutKindV1.Relation,
                        ["token-equals"],
                        [
                            new ExpectedLayoutEdgeV1(
                                LayoutRoleV1.Left,
                                new ExpectedLayoutNodeV1(
                                    LayoutKindV1.Sequence,
                                    [],
                                    tokens.Take(3).Select(token => new ExpectedLayoutEdgeV1(
                                        LayoutRoleV1.Item,
                                        new ExpectedLayoutNodeV1(
                                            LayoutKindV1.Token,
                                            [token.TokenId],
                                            []))).ToArray())),
                        ]),
                    evaluation)),
        ],
        new ExpectedSheetV1(
        [
            new ExpectedSheetNodeV1(
                "region-query",
                CorpusSheetRoleV1.Query,
                null,
                [],
                false,
                evaluation),
        ],
        ["region-query"],
        ["region-query"]));
        return new ExpressionCaseV1(
            CorpusFormatV1.CaseFormat,
            CorpusFormatV1.SchemaVersion,
            "phase-5.5-v1",
            1,
            "synthetic-product-runtime-001",
            CorpusPartitionV1.Development,
            new CaptureMetadataV1(
                CorpusCaptureSourceV1.Synthetic,
                CorpusDataClassificationV1.PublicSynthetic,
                "synthetic-writer",
                "synthetic-product-session-001",
                CorpusDeviceClassV1.Synthetic,
                CorpusPressureModeV1.Normalized,
                CorpusCaptureApiV1.HandAuthored,
                "product-runtime-v1",
                null),
            strokes,
            strokes.Select(stroke => stroke.StrokeId).ToArray(),
            [new RecognizeStepV1("recognize-query", page)]);
    }

    private static ExpressionCaseV1 QueryWithTaffyCase()
    {
        ExpressionCaseV1 baseline = QueryCase();
        var trialEvaluation = new ExpectedEvaluationV1(
            CorpusEvaluationKindV1.Number,
            true,
            "3");
        var trialSheet = new ExpectedSheetV1(
        [
            new ExpectedSheetNodeV1(
                "region-query",
                CorpusSheetRoleV1.Query,
                null,
                [],
                false,
                trialEvaluation),
        ],
        ["region-query"],
        ["region-query"]);
        return baseline with
        {
            Steps =
            [
                baseline.Steps.Single(),
                new TaffyProbeStepV1(
                    "taffy-query",
                    "region-query",
                    [
                        new LayoutPathSegmentV1(LayoutRoleV1.Left, 0),
                        new LayoutPathSegmentV1(LayoutRoleV1.Item, 0),
                    ],
                    ["stroke-1"],
                    new CorpusPointV1(5, 5),
                    CumulativeScreenDeltaX: 14,
                    CanvasScale: 1,
                    TrialLatex: "2+1=",
                    ExpectedSheet: trialSheet),
            ],
        };
    }

    private static ExpressionCaseV1 QueryWithPersistenceCase()
    {
        ExpressionCaseV1 baseline = QueryCase();
        ExpectedPageV1 page = Assert.IsType<RecognizeStepV1>(baseline.Steps.Single()).Expected;
        return baseline with
        {
            CaseId = "synthetic-product-persistence-001",
            Steps =
            [
                baseline.Steps.Single(),
                new SaveStepV1("save-a", "page", CorpusSaveModeV1.Explicit),
                new EraseStepV1("erase-first", ["stroke-1"]),
                new SaveStepV1("autosave-b", "page", CorpusSaveModeV1.Autosave),
                new RecoverStepV1(
                    "recover-corrupt-a",
                    "page",
                    CorpusRecoveryDamageV1.CorruptCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    page),
                new ReopenStepV1(
                    "reopen-still-damaged",
                    "page",
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    page),
                new CloseFlushStepV1("flush-recovered-a", "page"),
                new RecoverStepV1(
                    "recover-stale-temp",
                    "page",
                    CorpusRecoveryDamageV1.StaleTemporaryCandidate,
                    CorpusOpenStatusV1.OpenedCurrent,
                    page),
                new RecoverStepV1(
                    "recover-missing-current",
                    "page",
                    CorpusRecoveryDamageV1.MissingCurrent,
                    CorpusOpenStatusV1.BackupRecoveryCandidate,
                    page),
            ],
        };
    }

    private static ExpressionCaseV1 EmptyPersistenceCase()
    {
        ExpressionCaseV1 baseline = QueryCase();
        var page = new ExpectedPageV1([], new ExpectedSheetV1([], [], []));
        return baseline with
        {
            CaseId = "synthetic-product-empty-persistence-001",
            Strokes = [],
            InitialStrokeIds = [],
            Steps =
            [
                new RecognizeStepV1("recognize-empty", page),
                new SaveStepV1("save-empty", "page", CorpusSaveModeV1.Explicit),
                new ReopenStepV1(
                    "reopen-empty",
                    "page",
                    CorpusOpenStatusV1.OpenedCurrent,
                    page),
            ],
        };
    }

    private static CorpusStrokeV1 Stroke(string id, double x) => new(
        id,
        null,
        [
            new CorpusSampleV1(x, 0, 0, 0.5),
            new CorpusSampleV1(x + 10, 10, 10, 0.5),
        ]);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
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
                    .Where(timer => !timer.Disposed && timer.Due is not null && timer.Due <= target)
                    .OrderBy(timer => timer.Due)
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

        private sealed class FakeTimer(TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public bool Disposed { get; private set; }

            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class GeometryClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            double x = strokes.SelectMany(stroke => stroke.Samples).Min(sample => sample.X);
            string label = x switch
            {
                < 15 => "1",
                < 45 => "+",
                < 75 => "1",
                _ => "=",
            };
            return new SymbolPrediction(label, 0.99);
        }
    }

    private sealed class FixedClassifier(SymbolPrediction prediction) : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context) => prediction;
    }

    /// <summary>Reads the same four stroke x-positions <see cref="Stroke"/> lays out as <c>"(","x","+","1"</c> — an unmatched open bracket the real spatial grammar refuses.</summary>
    private sealed class UnmatchedOpenBracketClassifier : ISymbolClassifier
    {
        public SymbolPrediction Classify(IReadOnlyList<Stroke> strokes, SymbolContext context)
        {
            double x = strokes.SelectMany(stroke => stroke.Samples).Min(sample => sample.X);
            string label = x switch
            {
                < 15 => "(",
                < 45 => "x",
                < 75 => "+",
                _ => "1",
            };
            return new SymbolPrediction(label, 0.99);
        }
    }

    private sealed class AnyGlyphSource : IGlyphSource
    {
        public IReadOnlyList<Stroke>? GetGlyph(string symbol, Random random) =>
        [
            new Stroke(Guid.NewGuid(),
            [
                new StrokeSample(0.2, 0.1, TimeSpan.Zero, 0.4),
                new StrokeSample(0.8, 0.9, TimeSpan.FromMilliseconds(10), 0.6),
            ]),
        ];
    }
}
