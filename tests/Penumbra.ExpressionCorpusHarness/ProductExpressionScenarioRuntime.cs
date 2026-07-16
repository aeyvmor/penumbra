using System.Text;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Runtime;
using Penumbra.Sheet;

namespace Penumbra.ExpressionCorpus;

/// <summary>Builds corpus sessions from the same Recognition/CAS/Sheet transaction used by the App.</summary>
public sealed class ProductExpressionScenarioRuntimeFactory : IExpressionScenarioRuntimeFactory, IDisposable
{
    private readonly ISymbolClassifier _classifier;
    private readonly RecognitionCalibration _calibration;
    private readonly TimeProvider _timeProvider;
    private readonly HandwritingSynthesizer _synthesizer;
    private readonly bool _ownsClassifier;
    private int _disposed;

    /// <summary>Creates the real shipped ONNX runtime from its model directory.</summary>
    public ProductExpressionScenarioRuntimeFactory(string? modelDirectory = null)
    {
        modelDirectory = Path.GetFullPath(
            modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "Models"));
        var classifier = new OnnxSymbolClassifier(modelDirectory);
        _classifier = classifier;
        _calibration = classifier.Calibration;
        _timeProvider = TimeProvider.System;
        _synthesizer = CreateShippedSynthesizer();
        _ownsClassifier = true;
        ModelFingerprint = RecognitionArtifactFingerprint.Compute(modelDirectory);
    }

    /// <summary>
    /// Creates a deterministic product-pipeline fixture around an injected classifier. Segmentation,
    /// grammar, CAS, Sheet, and page transaction remain the real implementations.
    /// </summary>
    public ProductExpressionScenarioRuntimeFactory(
        ISymbolClassifier classifier,
        RecognitionCalibration calibration,
        string modelFingerprint,
        TimeProvider? timeProvider = null,
        HandwritingSynthesizer? synthesizer = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(calibration);
        if (!IsSha256(modelFingerprint))
        {
            throw new ArgumentException("Model fingerprint must be a nonzero SHA-256.", nameof(modelFingerprint));
        }
        if (!double.IsFinite(calibration.MinConfidence)
            || calibration.MinConfidence is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(calibration),
                "Recognition threshold must be finite and in (0, 1].");
        }

        _classifier = classifier;
        _calibration = calibration;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _synthesizer = synthesizer ?? CreateShippedSynthesizer();
        ModelFingerprint = modelFingerprint.ToLowerInvariant();
    }

    public string PipelineFingerprint => RecognitionPipelineFingerprint.Current;

    public string ModelFingerprint { get; }

    public double RecognitionThreshold => _calibration.MinConfidence;

    public IExpressionScenarioRuntime Create(
        ExpressionScenarioInputV1 input,
        ILocalMetricsSink metrics)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(metrics);

        var segmenter = new OverlapStrokeSegmenter();
        var recognizer = new ExpressionRecognizer(
            segmenter,
            new RegionSegmenter(segmenter),
            _classifier,
            metrics,
            _timeProvider);
        var sheet = new SheetGraph(
            new AngouriMathEvaluator(),
            new AngouriMathExpressionAnalyzer(),
            metrics,
            _timeProvider);
        var page = new PageRecognitionSession(recognizer, sheet, _calibration.MinConfidence);
        return new ProductExpressionScenarioRuntime(
            input,
            page,
            _calibration.MinConfidence,
            _synthesizer,
            _timeProvider,
            metrics);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_ownsClassifier && _classifier is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit)
        && value.Any(character => character != '0');

    private static HandwritingSynthesizer CreateShippedSynthesizer()
    {
        string fontPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "Caveat-VariableFont_wght.ttf");
        IReadOnlyList<IGlyphSource> sources = File.Exists(fontPath)
            ? [new CaveatGlyphSource(fontPath)]
            : Array.Empty<IGlyphSource>();
        return new HandwritingSynthesizer(sources);
    }
}

internal sealed class ProductExpressionScenarioRuntime : IExpressionScenarioRuntime
{
    internal static readonly TimeSpan AutosaveQuietPeriod = TimeSpan.FromSeconds(1.5);

    private static readonly string StorageParentPath = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "penumbra-expression-corpus-runtime"));

    private readonly PageRecognitionSession _page;
    private readonly InkDocument _document = new();
    private readonly IReadOnlyDictionary<string, Stroke> _templates;
    private readonly Dictionary<Guid, string> _aliasByStrokeId;
    private readonly Dictionary<Guid, SynthesizedHandwriting> _answers = new();
    private readonly double _recognitionThreshold;
    private readonly HandwritingSynthesizer _synthesizer;
    private readonly PageTaffyController _taffy;
    private readonly TimeProvider _timeProvider;
    private readonly FileSystemPageStore _pageStore;
    private readonly PageAutosaveCoordinator _autosave;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly string _storageRootPath;
    private readonly Dictionary<string, string> _storePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _committedStoreSlots = new(StringComparer.Ordinal);
    private long _saveGeneration;
    private int _disposed;

    public ProductExpressionScenarioRuntime(
        ExpressionScenarioInputV1 input,
        PageRecognitionSession page,
        double recognitionThreshold,
        HandwritingSynthesizer synthesizer,
        TimeProvider timeProvider,
        ILocalMetricsSink metrics)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(synthesizer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(metrics);
        _page = page;
        _recognitionThreshold = recognitionThreshold;
        _synthesizer = synthesizer;
        _timeProvider = timeProvider;

        var templates = new Dictionary<string, Stroke>(StringComparer.Ordinal);
        _aliasByStrokeId = new Dictionary<Guid, string>();
        foreach (CorpusStrokeV1 source in input.Strokes)
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            }
            while (_aliasByStrokeId.ContainsKey(id));

            var stroke = new Stroke(
                id,
                source.Samples.Select(sample => new StrokeSample(
                    sample.X,
                    sample.Y,
                    TimeSpan.FromTicks(sample.ElapsedTicks),
                    sample.Pressure)).ToArray());
            templates.Add(source.StrokeId, stroke);
            _aliasByStrokeId.Add(id, source.StrokeId);
        }
        _templates = templates;

        Stroke[] initial = input.InitialStrokeIds.Select(ResolveTemplate).ToArray();
        PenumbraDocument document = PenumbraDocumentSerializer.CreateEmpty() with
        {
            Version = PenumbraDocumentSerializer.SchemaVersion,
            Strokes = initial,
            StrokeMetadata = initial.Select(stroke => new PersistedStrokeMetadata(
                stroke.Id,
                StrokeOriginNames.UserInk)).ToArray(),
            RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
        };
        _document.Load(document);
        _taffy = new PageTaffyController(
            _page,
            _document,
            _synthesizer,
            timeProvider,
            metrics);
        _storageRootPath = CreateStorageRoot();
        _pageStore = new FileSystemPageStore(metrics, timeProvider);
        _autosave = new PageAutosaveCoordinator(
            _pageStore,
            AutosaveQuietPeriod,
            timeProvider,
            metrics);
    }

    internal string StorageRootPath => _storageRootPath;

    internal long AutosaveLatestRevision => _autosave.LatestRevision;

    internal long AutosaveCommittedRevision => _autosave.CommittedRevision;

    public async Task<StepActualV1> ApplyAsync(
        ScenarioActionV1 action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(action);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        CancellationToken operationToken = operationCancellation.Token;
        await _operationGate.WaitAsync(operationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            operationToken.ThrowIfCancellationRequested();
            switch (action)
            {
                case AddInkActionV1 add:
                    _taffy.End();
                    _document.AddStrokes(add.StrokeIds.Select(ResolveTemplate), StrokeOriginKind.UserInk);
                    return new MutationActualV1(DocumentState());
                case EraseActionV1 erase:
                    _taffy.End();
                    _document.EraseStrokes(erase.StrokeIds.Select(ResolveStrokeId));
                    return new MutationActualV1(DocumentState());
                case RewriteActionV1 rewrite:
                    _taffy.End();
                    _document.ReplaceStrokes(
                        rewrite.RemovedStrokeIds.Select(ResolveStrokeId),
                        rewrite.AddedStrokeIds.Select(ResolveTemplate),
                        StrokeOriginKind.UserInk);
                    return new MutationActualV1(DocumentState());
                case UndoActionV1:
                    _taffy.End();
                    _document.Undo();
                    return new MutationActualV1(DocumentState());
                case RedoActionV1:
                    _taffy.End();
                    _document.Redo();
                    return new MutationActualV1(DocumentState());
                case RecognizeActionV1:
                    _taffy.End();
                    PageRecognitionCandidate candidate = await _page
                        .RecognizeAsync(_document.Strokes, operationToken);
                    PageRecognitionApplication application = _page.Apply(candidate);
                    RefreshAnswers();
                    _page.Commit(application);
                    return new RecognizeActualV1(ToActualPage(application));
                case StampActionV1 stamp:
                    return Stamp(stamp);
                case TaffyProbeActionV1 taffy:
                    return TaffyProbe(taffy);
                case SaveActionV1 save:
                    return await SaveAsync(save, operationToken);
                case CloseFlushActionV1 close:
                    return await CloseFlushAsync(close, operationToken);
                case ReopenActionV1 reopen:
                    return await ReopenAsync(reopen, operationToken);
                case RecoverActionV1 recover:
                    return await RecoverAsync(recover, operationToken);
                case GraphActionV1:
                    return new CapabilityUnavailableActualV1(CorpusCapabilityV1.Graph);
                default:
                    return new FailedStepActualV1(
                        CorpusFailureCategoryV1.Infrastructure,
                        "unsupported_scenario_action");
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        await _operationGate.WaitAsync();
        try
        {
            _taffy.End();
            try
            {
                await _autosave.DisposeAsync();
            }
            finally
            {
                DeleteStorageRoot();
            }
        }
        finally
        {
            _operationGate.Release();
            _lifetimeCancellation.Dispose();
        }
    }

    private async Task<StepActualV1> SaveAsync(
        SaveActionV1 action,
        CancellationToken cancellationToken)
    {
        _taffy.End();
        if (action.Mode == CorpusSaveModeV1.Autosave)
        {
            return await AutosaveAsync(action, cancellationToken);
        }
        if (action.Mode != CorpusSaveModeV1.Explicit)
        {
            throw new InvalidOperationException("Scenario requested an undefined save mode.");
        }

        string path = StorePath(action.StoreSlot, create: true)!;
        long generation = checked(++_saveGeneration);
        PageSaveResult result = await _pageStore.SaveAsync(
            PageDocumentSnapshot.Create(_document, _page),
            path,
            generation,
            PageSaveKind.Explicit,
            cancellationToken);
        bool completed = result.Status == PageSaveStatus.Committed
            && result.Generation == generation;
        if (completed)
        {
            _committedStoreSlots.Add(action.StoreSlot);
        }
        return new PersistenceWriteActualV1(completed);
    }

    private async Task<StepActualV1> AutosaveAsync(
        SaveActionV1 action,
        CancellationToken cancellationToken)
    {
        string path = StorePath(action.StoreSlot, create: true)!;
        var completion = new TaskCompletionSource<PageAutosaveStateChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long revision = 0;
        void Observe(object? _, PageAutosaveStateChangedEventArgs state)
        {
            if (state.Revision == revision)
            {
                completion.TrySetResult(state);
            }
        }

        _autosave.StateChanged += Observe;
        try
        {
            revision = _autosave.Schedule(
                PageDocumentSnapshot.Create(_document, _page),
                path);
            if (_autosave.CommittedRevision < revision && _autosave.LastFailure is null)
            {
                await completion.Task.WaitAsync(cancellationToken);
            }

            bool completed = _autosave.CommittedRevision >= revision;
            if (completed)
            {
                _committedStoreSlots.Add(action.StoreSlot);
            }
            return new PersistenceWriteActualV1(completed);
        }
        finally
        {
            _autosave.StateChanged -= Observe;
        }
    }

    private async Task<StepActualV1> CloseFlushAsync(
        CloseFlushActionV1 action,
        CancellationToken cancellationToken)
    {
        _taffy.End();
        string? path = StorePath(action.StoreSlot, create: false);
        if (path is null || !_committedStoreSlots.Contains(action.StoreSlot))
        {
            return PersistenceFailure("close_flush_unknown_slot");
        }

        long revision = _autosave.Schedule(
            PageDocumentSnapshot.Create(_document, _page),
            path);
        await _autosave.FlushAsync(cancellationToken);
        bool completed = _autosave.CommittedRevision >= revision;
        return new PersistenceWriteActualV1(completed);
    }

    private async Task<StepActualV1> ReopenAsync(
        ReopenActionV1 action,
        CancellationToken cancellationToken)
    {
        string? path = StorePath(action.StoreSlot, create: false);
        return path is null || !_committedStoreSlots.Contains(action.StoreSlot)
            ? PersistenceFailure("reopen_unknown_slot")
            : await OpenStoreAsync(path, staleTemporaryPath: null, cancellationToken);
    }

    private async Task<StepActualV1> RecoverAsync(
        RecoverActionV1 action,
        CancellationToken cancellationToken)
    {
        string? path = StorePath(action.StoreSlot, create: false);
        if (path is null || !_committedStoreSlots.Contains(action.StoreSlot))
        {
            return PersistenceFailure("recover_unknown_slot");
        }

        string? staleTemporaryPath = null;
        switch (action.Damage)
        {
            case CorpusRecoveryDamageV1.CorruptCurrent:
                await File.WriteAllTextAsync(
                    path,
                    "{not-a-penumbra-document",
                    Encoding.UTF8,
                    cancellationToken);
                break;
            case CorpusRecoveryDamageV1.MissingCurrent:
                File.Delete(path);
                break;
            case CorpusRecoveryDamageV1.StaleTemporaryCandidate:
                staleTemporaryPath = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    $"{Path.GetFileName(path)}.penumbra-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(
                    staleTemporaryPath,
                    PenumbraDocumentSerializer.Serialize(PenumbraDocumentSerializer.CreateEmpty() with
                    {
                        Version = PenumbraDocumentSerializer.SchemaVersion,
                        RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
                    }),
                    Encoding.UTF8,
                    cancellationToken);
                File.SetLastWriteTimeUtc(
                    staleTemporaryPath,
                    (_timeProvider.GetUtcNow() - TimeSpan.FromDays(2)).UtcDateTime);
                break;
            default:
                return PersistenceFailure("undefined_recovery_damage");
        }

        return await OpenStoreAsync(path, staleTemporaryPath, cancellationToken);
    }

    private async Task<StepActualV1> OpenStoreAsync(
        string path,
        string? staleTemporaryPath,
        CancellationToken cancellationToken)
    {
        _taffy.End();
        PageOpenResult opened = await _pageStore.OpenAsync(path, cancellationToken);
        if (staleTemporaryPath is not null && File.Exists(staleTemporaryPath))
        {
            return PersistenceFailure("stale_temporary_not_cleaned");
        }

        CorpusOpenStatusV1 status = OpenStatus(opened.Status);
        if (opened.Document is null)
        {
            return new PersistenceOpenActualV1(status, DocumentState(), Page: null);
        }

        _answers.Clear();
        _document.Load(opened.Document);

        // Matching-v4 hints seed stable segmentation identity but carry a forced-refresh marker: App and
        // corpus both reclassify on load, so cached disagreement can never become result authority.
        _page.Clear();
        _page.ReplaceCache(PageRecognitionCache.BuildValidLoadCache(opened.Document));
        PageRecognitionCandidate candidate = await _page.RecognizeAsync(
            _document.Strokes,
            cancellationToken);
        PageRecognitionApplication application = _page.Apply(candidate);
        RefreshAnswers();
        _page.Commit(application);
        return new PersistenceOpenActualV1(
            status,
            DocumentState(),
            ToActualPage(application));
    }

    private string? StorePath(string storeSlot, bool create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeSlot);
        if (_storePaths.TryGetValue(storeSlot, out string? existing))
        {
            return existing;
        }
        if (!create)
        {
            return null;
        }

        string path = Path.Combine(_storageRootPath, $"slot-{_storePaths.Count:D4}.pen");
        _storePaths.Add(storeSlot, path);
        return path;
    }

    private static CorpusOpenStatusV1 OpenStatus(PageOpenStatus status) => status switch
    {
        PageOpenStatus.Current => CorpusOpenStatusV1.OpenedCurrent,
        PageOpenStatus.BackupRecoveryCandidate => CorpusOpenStatusV1.BackupRecoveryCandidate,
        PageOpenStatus.NotFound => CorpusOpenStatusV1.NotFound,
        PageOpenStatus.Unrecoverable => CorpusOpenStatusV1.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined page-open status."),
    };

    private static FailedStepActualV1 PersistenceFailure(string code) => new(
        CorpusFailureCategoryV1.Persistence,
        code);

    private static string CreateStorageRoot()
    {
        Directory.CreateDirectory(StorageParentPath);
        string root = Path.GetFullPath(Path.Combine(
            StorageParentPath,
            Guid.NewGuid().ToString("N")));
        if (!PathEquals(Path.GetDirectoryName(root), StorageParentPath))
        {
            throw new IOException("Corpus storage root escaped its owned temporary parent.");
        }
        Directory.CreateDirectory(root);
        return root;
    }

    private void DeleteStorageRoot()
    {
        string fullRoot = Path.GetFullPath(_storageRootPath);
        if (!PathEquals(Path.GetDirectoryName(fullRoot), StorageParentPath))
        {
            throw new IOException("Refused to delete a directory outside the corpus runtime's owned root.");
        }
        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }
    }

    private static bool PathEquals(string? left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void RefreshAnswers()
    {
        SheetNode[] answerable = _page.SheetNodes
            .Where(PageAnswerMaterializer.IsAnswerableQuery)
            .ToArray();
        HashSet<Guid> valid = answerable.Select(node => node.Id).ToHashSet();
        foreach (Guid stale in _answers.Keys.Where(id => !valid.Contains(id)).ToArray())
        {
            _answers.Remove(stale);
        }

        foreach (SheetNode node in answerable)
        {
            SynthesizedHandwriting? answer = PageAnswerMaterializer.TrySynthesize(
                node.Result!.DisplayText,
                node.Tokens,
                _synthesizer);
            if (answer is null)
            {
                _answers.Remove(node.Id);
            }
            else
            {
                _answers[node.Id] = answer;
            }
        }
    }

    private StampActualV1 Stamp(StampActionV1 action)
    {
        _taffy.End();
        Guid ownerId = Guid.TryParse(action.SourceRegionHandle, out Guid parsed)
            ? parsed
            : Guid.Empty;
        _answers.TryGetValue(ownerId, out SynthesizedHandwriting? answer);
        PageStampResult result = PageStampTransaction.Apply(
            _document,
            _page.AcceptedRegions,
            ownerId,
            answer?.Strokes,
            action.GestureDelta.X,
            action.GestureDelta.Y,
            action.DropPoint.X,
            action.DropPoint.Y);

        foreach (Stroke stroke in result.AddedStrokes)
        {
            _aliasByStrokeId.Add(stroke.Id, stroke.Id.ToString("D"));
        }
        if (result.HideSourceAnswer)
        {
            _answers.Remove(ownerId);
        }

        return new StampActualV1(
            StampDecision(result.Decision),
            result.AppliedScale,
            result.SourceStrokes
                .Select(stroke => CorpusStroke(stroke, stroke.Id.ToString("D")))
                .ToArray(),
            result.RemovedStrokeIds.Select(StrokeAlias).ToArray(),
            result.AddedStrokes
                .Select(stroke => CorpusStroke(stroke, StrokeAlias(stroke.Id)))
                .ToArray(),
            DocumentState());
    }

    private StepActualV1 TaffyProbe(TaffyProbeActionV1 action)
    {
        bool began = _taffy.BeginAt(
            action.HitPointWorld.X,
            action.HitPointWorld.Y,
            action.CanvasScale,
            _answers.Keys.ToHashSet(),
            _answers.Values.ToArray());
        if (!began)
        {
            return new FailedStepActualV1(
                CorpusFailureCategoryV1.UiIntegration,
                "taffy_hit_refused");
        }

        try
        {
            PageTaffyUpdateResult result = _taffy.Update(action.CumulativeScreenDeltaX);
            if (!result.Probed)
            {
                return new FailedStepActualV1(
                    CorpusFailureCategoryV1.UiIntegration,
                    "taffy_probe_refused");
            }
            return new TaffyProbeActualV1(
                result.TrialLatex!,
                ToActualProbeSheet(result.ProbeReport!));
        }
        finally
        {
            _taffy.End();
        }
    }

    private ActualSheetV1 ToActualProbeSheet(SheetProbeReport report)
    {
        Dictionary<Guid, EvaluationResult> trialResults = report.Entries
            .ToDictionary(entry => entry.Node.Id, entry => entry.TrialResult);
        Dictionary<Guid, RegionRecognition> recognitionById = _page.AcceptedRegions
            .ToDictionary(region => region.Region.Id);
        ActualSheetNodeV1[] nodes = _page.SheetNodes
            .OrderBy(node => node.Region?.Y ?? double.PositiveInfinity)
            .ThenBy(node => node.Id)
            .Select(node =>
            {
                RegionRecognition source = recognitionById[node.Id];
                EvaluationResult? result = trialResults.TryGetValue(node.Id, out EvaluationResult? trial)
                    ? trial
                    : node.Result;
                return new ActualSheetNodeV1(
                    RegionHandle(node.Id),
                    source.Region.StrokeIds.Select(StrokeAlias).ToArray(),
                    SheetRole(node.Role),
                    node.DefinedSymbol,
                    node.FreeVariables.Order(StringComparer.Ordinal).ToArray(),
                    node.IsConflict,
                    Evaluation(result));
            })
            .ToArray();
        string[] changed = report.Entries
            .Where(entry => !Equals(entry.Node.Result, entry.TrialResult))
            .Select(entry => RegionHandle(entry.Node.Id))
            .ToArray();
        string[] affected = report.Entries
            .Select(entry => RegionHandle(entry.Node.Id))
            .ToArray();
        return new ActualSheetV1(nodes, changed, affected);
    }

    private static CorpusStampDecisionV1 StampDecision(PageStampDecision decision) => decision switch
    {
        PageStampDecision.Append => CorpusStampDecisionV1.Append,
        PageStampDecision.Replace => CorpusStampDecisionV1.Replace,
        PageStampDecision.Refuse => CorpusStampDecisionV1.Refuse,
        _ => throw new InvalidOperationException("Runtime returned an unknown stamp decision."),
    };

    private static CorpusStrokeV1 CorpusStroke(Stroke stroke, string alias) => new(
        alias,
        null,
        stroke.Samples.Select(sample => new CorpusSampleV1(
            sample.X,
            sample.Y,
            sample.Time.Ticks,
            sample.Pressure)).ToArray());

    private ActualPageV1 ToActualPage(PageRecognitionApplication application)
    {
        HashSet<Guid> accepted = application.AcceptedRegions
            .Select(region => region.Region.Id)
            .ToHashSet();
        Dictionary<Guid, RegionRecognition> recognitionById = application.Regions
            .ToDictionary(region => region.Region.Id);

        ActualRegionV1[] regions = application.Regions.Select(region => new ActualRegionV1(
            RegionHandle(region.Region.Id),
            region.Region.StrokeIds.Select(StrokeAlias).ToArray(),
            accepted.Contains(region.Region.Id)
                ? AcceptedOutcome(region)
                : RefusedOutcome(region),
            Bounds(region.Region.Bounds))).ToArray();

        ActualSheetNodeV1[] nodes = _page.SheetNodes
            .OrderBy(node => node.Region?.Y ?? double.PositiveInfinity)
            .ThenBy(node => node.Id)
            .Select(node =>
            {
                RegionRecognition source = recognitionById[node.Id];
                return new ActualSheetNodeV1(
                    RegionHandle(node.Id),
                    source.Region.StrokeIds.Select(StrokeAlias).ToArray(),
                    SheetRole(node.Role),
                    node.DefinedSymbol,
                    node.FreeVariables.Order(StringComparer.Ordinal).ToArray(),
                    node.IsConflict,
                    Evaluation(node.Result));
            })
            .ToArray();
        var sheet = new ActualSheetV1(
            nodes,
            application.RecomputeReport.ChangedResultNodes
                .Select(node => RegionHandle(node.Id))
                .ToArray(),
            application.RecomputeReport.CausallyAffectedNodes
                .Select(node => RegionHandle(node.Id))
                .ToArray());
        return new ActualPageV1(regions, sheet);
    }

    private AcceptedRegionActualV1 AcceptedOutcome(RegionRecognition region)
    {
        ActualTokenV1[] tokens = region.Result.Tokens.Select(token => new ActualTokenV1(
            token.Latex,
            token.SourceStrokeIds.Select(StrokeAlias).ToArray(),
            token.Confidence,
            token.Rejected)).ToArray();
        SheetNode? node = _page.SheetNodes.SingleOrDefault(candidate => candidate.Id == region.Region.Id);
        LayoutNode root = region.Result.ParseOutcome?.Root
            ?? throw new InvalidOperationException(
                "An accepted product recognition result must carry its authoritative layout root.");
        return new AcceptedRegionActualV1(
            region.Result.Latex,
            tokens,
            ActualLayout(root, region.Result.Tokens),
            Evaluation(node?.Result));
    }

    private RefusedRegionActualV1 RefusedOutcome(RegionRecognition region)
    {
        if (region.Result.Tokens.Any(token => token.Rejected))
        {
            return new RefusedRegionActualV1(
                CorpusFailureCategoryV1.SymbolClassification,
                CorpusRefusalCodeV1.OutOfDistribution);
        }
        if (region.Result.Tokens.Any(token =>
                token.Confidence < _recognitionThreshold))
        {
            return new RefusedRegionActualV1(
                CorpusFailureCategoryV1.SymbolClassification,
                CorpusRefusalCodeV1.LowConfidence);
        }

        // Every symbol cleared the confidence/OOD gate. RecognitionGate checks that gate FIRST and only
        // then a structural ParseOutcome (RecognitionGate.cs), so any remaining refusal reaching here came
        // from the spatial grammar's own verdict — report the real reason instead of a fixed guess.
        if (region.Result.ParseOutcome is { IsAccepted: false } outcome)
        {
            (CorpusFailureCategoryV1 stage, CorpusRefusalCodeV1 code) = StructuralRefusalCategory(outcome.Reason);
            return new RefusedRegionActualV1(stage, code);
        }

        // No structural opinion and every symbol was confident: unreachable through the real gate today
        // (RecognitionGate has no third refusal path), kept as an honest conservative default rather than
        // silently mislabeling or crashing on an unrecognized shape.
        return new RefusedRegionActualV1(
            CorpusFailureCategoryV1.Assembly,
            CorpusRefusalCodeV1.UnsupportedNotation);
    }

    /// <summary>
    /// Maps the spatial grammar's typed <see cref="ParseRefusalReason"/> to the corpus schema's real
    /// failure-category/refusal-code pair (<see cref="CorpusRefusalSemanticsV1"/> pins which pairs are
    /// valid). Reasons that are raw geometry/position ambiguity — bracket pairing, script placement,
    /// fraction/radical ownership, function-word tightness, digit-grouping, glyph near-ties, and the
    /// ownership-validator's own lost/duplicate-stroke safety net — map to
    /// <see cref="CorpusFailureCategoryV1.SpatialRelation"/>. Reasons that only fail once tokens try to
    /// ASSEMBLE into one tree — more than one relation on a line, or an unsupported/incomplete construct the
    /// span-clustering stage can't place (<c>\sum</c>/<c>\int</c>, a dangling operator, an empty
    /// candidate/group) — map to <see cref="CorpusFailureCategoryV1.Assembly"/>.
    /// </summary>
    internal static (CorpusFailureCategoryV1 Stage, CorpusRefusalCodeV1 Reason) StructuralRefusalCategory(
        ParseRefusalReason reason) => reason switch
    {
        ParseRefusalReason.UnmatchedBracket =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.MalformedStructure),
        ParseRefusalReason.UncertainScript =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        ParseRefusalReason.GeneralSubscript =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.UnsupportedNotation),
        ParseRefusalReason.AmbiguousFractionOwnership =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        ParseRefusalReason.EmptyRadicalOwnership =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        ParseRefusalReason.AmbiguousFunctionWord =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        ParseRefusalReason.DigitProductAmbiguity =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        ParseRefusalReason.LowMargin =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.SpatialAmbiguity),
        ParseRefusalReason.LostStroke =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.UnownedStroke),
        ParseRefusalReason.DoubleOwnership =>
            (CorpusFailureCategoryV1.SpatialRelation, CorpusRefusalCodeV1.DuplicateStrokeOwnership),
        ParseRefusalReason.UnsupportedRelation =>
            (CorpusFailureCategoryV1.Assembly, CorpusRefusalCodeV1.UnsupportedNotation),
        ParseRefusalReason.UnsupportedNotation =>
            (CorpusFailureCategoryV1.Assembly, CorpusRefusalCodeV1.UnsupportedNotation),
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Accepted or unknown parse reasons cannot describe a refusal."),
    };

    /// <summary>Maps Core's authoritative immutable layout into the corpus result contract.</summary>
    internal static ActualLayoutNodeV1 ActualLayout(
        LayoutNode root,
        IReadOnlyList<RecognizedToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(tokens);
        var indexes = new Dictionary<RecognizedToken, int>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < tokens.Count; index++)
        {
            RecognizedToken token = tokens[index]
                ?? throw new ArgumentException("The recognition token list contains null.", nameof(tokens));
            if (!indexes.TryAdd(token, index))
            {
                throw new InvalidOperationException(
                    "The recognition token list contains the same token reference more than once.");
            }
        }

        return Map(root, indexes);
    }

    private static ActualLayoutNodeV1 Map(
        LayoutNode node,
        IReadOnlyDictionary<RecognizedToken, int> indexes) => node switch
    {
        LeafNode leaf => TokenNode(leaf.Token, indexes),
        SequenceNode sequence => new ActualLayoutNodeV1(
            LayoutKindV1.Sequence,
            [],
            sequence.Children
                .Select(child => Edge(LayoutRoleV1.Item, child, indexes))
                .ToArray()),
        ImplicitProductNode product => new ActualLayoutNodeV1(
            LayoutKindV1.ImplicitProduct,
            [],
            product.Factors
                .Select(factor => Edge(LayoutRoleV1.Factor, factor, indexes))
                .ToArray()),
        ScriptNode script => new ActualLayoutNodeV1(
            LayoutKindV1.Script,
            [],
            OptionalEdges(
                Edge(LayoutRoleV1.Base, script.Base, indexes),
                script.Superscript is null
                    ? null
                    : Edge(LayoutRoleV1.Superscript, script.Superscript, indexes),
                script.Subscript is null
                    ? null
                    : Edge(LayoutRoleV1.Subscript, script.Subscript, indexes))),
        FractionNode fraction => new ActualLayoutNodeV1(
            LayoutKindV1.Fraction,
            [TokenIndex(fraction.BarToken, indexes)],
            [
                Edge(LayoutRoleV1.Numerator, fraction.Numerator, indexes),
                Edge(LayoutRoleV1.Denominator, fraction.Denominator, indexes),
            ]),
        RadicalNode radical => new ActualLayoutNodeV1(
            LayoutKindV1.Radical,
            [TokenIndex(radical.RadicalToken, indexes)],
            OptionalEdges(
                Edge(LayoutRoleV1.Radicand, radical.Radicand, indexes),
                radical.RootIndex is null
                    ? null
                    : Edge(LayoutRoleV1.RootIndex, radical.RootIndex, indexes))),
        DelimitedGroupNode group => new ActualLayoutNodeV1(
            LayoutKindV1.DelimitedGroup,
            [TokenIndex(group.OpenToken, indexes), TokenIndex(group.CloseToken, indexes)],
            [Edge(LayoutRoleV1.Body, group.Inner, indexes)]),
        FunctionCallNode function => new ActualLayoutNodeV1(
            LayoutKindV1.FunctionCall,
            [],
            [
                new ActualLayoutEdgeV1(LayoutRoleV1.Function, TokenSequence(function.NameTokens, indexes)),
                Edge(LayoutRoleV1.Argument, function.Argument, indexes),
            ]),
        RelationNode relation => new ActualLayoutNodeV1(
            LayoutKindV1.Relation,
            [TokenIndex(relation.RelationToken, indexes)],
            OptionalEdges(
                Edge(LayoutRoleV1.Left, relation.Left, indexes),
                relation.Right is null
                    ? null
                    : Edge(LayoutRoleV1.Right, relation.Right, indexes))),
        _ => throw new ArgumentOutOfRangeException(
            nameof(node), node.GetType().Name, "Unknown Core layout node kind."),
    };

    private static ActualLayoutNodeV1 TokenSequence(
        IReadOnlyList<RecognizedToken> tokens,
        IReadOnlyDictionary<RecognizedToken, int> indexes) => tokens.Count == 1
        ? TokenNode(tokens[0], indexes)
        : new ActualLayoutNodeV1(
            LayoutKindV1.Sequence,
            [],
            tokens.Select(token => new ActualLayoutEdgeV1(
                LayoutRoleV1.Item,
                TokenNode(token, indexes))).ToArray());

    private static ActualLayoutNodeV1 TokenNode(
        RecognizedToken token,
        IReadOnlyDictionary<RecognizedToken, int> indexes) => new(
            LayoutKindV1.Token,
            [TokenIndex(token, indexes)],
            []);

    private static ActualLayoutEdgeV1 Edge(
        LayoutRoleV1 role,
        LayoutNode node,
        IReadOnlyDictionary<RecognizedToken, int> indexes) => new(role, Map(node, indexes));

    private static ActualLayoutEdgeV1[] OptionalEdges(params ActualLayoutEdgeV1?[] edges) =>
        edges.OfType<ActualLayoutEdgeV1>().ToArray();

    private static int TokenIndex(
        RecognizedToken token,
        IReadOnlyDictionary<RecognizedToken, int> indexes) =>
        indexes.TryGetValue(token, out int index)
            ? index
            : throw new InvalidOperationException(
                "The accepted layout owns a token reference absent from the recognition result.");

    private ActualDocumentStateV1 DocumentState()
    {
        string[] live = _document.Strokes.Select(stroke => StrokeAlias(stroke.Id)).ToArray();
        string[] user = _document.Strokes
            .Where(stroke => _document.GetStrokeOrigin(stroke.Id) == StrokeOriginKind.UserInk)
            .Select(stroke => StrokeAlias(stroke.Id))
            .ToArray();
        string[] synthesized = _document.Strokes
            .Where(stroke => _document.GetStrokeOrigin(stroke.Id) == StrokeOriginKind.SynthesizedInk)
            .Select(stroke => StrokeAlias(stroke.Id))
            .ToArray();
        return new ActualDocumentStateV1(live, user, synthesized);
    }

    private Stroke ResolveTemplate(string alias) => _templates.TryGetValue(alias, out Stroke? stroke)
        ? stroke
        : throw new InvalidOperationException("Scenario referenced an undeclared stroke alias.");

    private Guid ResolveStrokeId(string alias)
    {
        if (_templates.TryGetValue(alias, out Stroke? template))
        {
            return template.Id;
        }
        if (Guid.TryParse(alias, out Guid id) && _aliasByStrokeId.ContainsKey(id))
        {
            return id;
        }
        throw new InvalidOperationException("Scenario referenced an unknown live stroke identity.");
    }

    private string StrokeAlias(Guid id) => _aliasByStrokeId.TryGetValue(id, out string? alias)
        ? alias
        : throw new InvalidOperationException("Product output referenced an unknown stroke identity.");

    private static string RegionHandle(Guid id) => id.ToString("D");

    private static CorpusBoundsV1 Bounds(InkBounds bounds) => new(
        bounds.X,
        bounds.Y,
        bounds.Width,
        bounds.Height);

    private static CorpusSheetRoleV1 SheetRole(NodeRole role) => role switch
    {
        NodeRole.Definition => CorpusSheetRoleV1.Definition,
        NodeRole.Query => CorpusSheetRoleV1.Query,
        NodeRole.Statement => CorpusSheetRoleV1.Statement,
        _ => throw new InvalidOperationException("Sheet returned an unknown node role."),
    };

    private static ExpectedEvaluationV1? Evaluation(EvaluationResult? result)
    {
        if (result is null)
        {
            return null;
        }
        string value = !string.IsNullOrWhiteSpace(result.Latex)
            ? result.Latex
            : result.DisplayText;
        return new ExpectedEvaluationV1(
            result.Kind switch
            {
                EvaluationKind.Pending => CorpusEvaluationKindV1.Pending,
                EvaluationKind.Number => CorpusEvaluationKindV1.Number,
                EvaluationKind.Symbolic => CorpusEvaluationKindV1.Symbolic,
                EvaluationKind.Solution => CorpusEvaluationKindV1.Solution,
                EvaluationKind.Boolean => CorpusEvaluationKindV1.Boolean,
                EvaluationKind.Error => CorpusEvaluationKindV1.Error,
                _ => throw new InvalidOperationException("CAS returned an unknown evaluation kind."),
            },
            result.IsComputed,
            value);
    }
}
