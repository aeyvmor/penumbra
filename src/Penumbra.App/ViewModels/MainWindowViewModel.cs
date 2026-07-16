using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Graphing;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Runtime;
using Penumbra.Sheet;

namespace Penumbra.App.ViewModels;

/// <summary>
/// Owns one page's raw ink, incremental recognition cache, reactive Sheet graph, and transient visuals.
/// The ownership boundary is deliberate: recognition output may enter Sheet, while synthesized answers
/// and causality ripples are overlay state and can never become recognizer input.
/// </summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly IReadOnlySet<Guid> NoStrokes = new HashSet<Guid>();
    private static long s_explicitSaveGeneration;
    private const string IdleHint = "Write expressions on separate lines — queries ending in '=' answer when you pause";

    internal static readonly TimeSpan LiveQuietPeriod = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan TaffyProbeFloor = PageTaffyController.ProbeFloor;
    internal static readonly TimeSpan AutosaveQuietPeriod = TimeSpan.FromSeconds(1.5);

    // s19 dogfood: an erase is usually the first half of "erase, then rewrite". Recomputing on the
    // normal quiet period races the user — the half-edited line ('x=' with its value gone) recognizes,
    // ripples the whole dependency chain, then everything recomputes AGAIN when the new value lands.
    // Stroke-removing edits therefore get a longer window; pen-down still cancels it instantly.
    internal static readonly TimeSpan EraseQuietPeriod = TimeSpan.FromSeconds(2.2);

    private readonly PageRecognitionSession _pageSession;
    private readonly PageTaffyController _taffy;
    private readonly SheetGraph _sheet;
    private readonly IGlyphBank? _glyphBank;
    private readonly HandwritingSynthesizer? _synthesizer;
    private readonly RecognitionCalibration _calibration;
    private readonly Debouncer _liveDebouncer;
    private readonly TimeProvider _time;
    private readonly ILocalMetricsSink _metrics;
    private readonly IPageStore? _pageStore;
    private readonly PageAutosaveCoordinator? _autosave;
    private readonly string? _recoveryPath;
    private readonly SemaphoreSlim _pageOperationGate = new(1, 1);
    private readonly Action<Action> _dispatchPersistenceState =
        static action => Dispatcher.UIThread.Post(action);
    private readonly HashSet<string> _bankedStrokeSets = new();
    private QuietPeriodMetricState? _quietPeriodMetric;
    private long _quietPeriodGeneration;
    private long _documentRevision;
    private long _durablySavedDocumentRevision;
    private int _recoveryInspectionCompleted;
    private int _cleanShutdownInProgress;

    private CancellationTokenSource? _recognitionCts;
    private long _recognitionGeneration;
    private long _visualSequence;
    private bool _suppressDocumentChanged;
    private int _lastKnownStrokeCount;
    private bool _disposed;

    public MainWindowViewModel()
        : this(
            new EmptyRegionRecognizer(),
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            glyphBank: null,
            synthesizer: null,
            calibration: RecognitionCalibration.Default)
    {
    }

    public MainWindowViewModel(
        IRegionRecognizer regionRecognizer,
        SheetGraph sheet,
        IGlyphBank? glyphBank = null,
        HandwritingSynthesizer? synthesizer = null,
        RecognitionCalibration? calibration = null,
        TimeProvider? time = null,
        ILocalMetricsSink? metrics = null,
        IPageStore? pageStore = null,
        string? recoveryPath = null,
        IGraphDetector? graphDetector = null,
        IDomainSampler? domainSampler = null)
    {
        ArgumentNullException.ThrowIfNull(regionRecognizer);
        ArgumentNullException.ThrowIfNull(sheet);

        _sheet = sheet;
        // Phase 6: graphing is presentation-adjacent page state. Hosts that pass nothing (design-time,
        // pre-Phase-6 tests) get the never-accepting detector, so the panel simply stays empty for them.
        GraphPanel = new GraphPanelViewModel(
            graphDetector ?? new NoOpGraphDetector(),
            domainSampler ?? new DomainSampler());
        _glyphBank = glyphBank;
        _synthesizer = synthesizer;
        _calibration = calibration ?? RecognitionCalibration.Default;
        _pageSession = new PageRecognitionSession(
            regionRecognizer,
            sheet,
            _calibration.MinConfidence);
        _time = time ?? TimeProvider.System;
        _metrics = metrics ?? NoOpLocalMetricsSink.Instance;
        _pageStore = pageStore;
        if (pageStore is not null)
        {
            _recoveryPath = Path.GetFullPath(recoveryPath ?? DefaultRecoveryPath());
            _autosave = new PageAutosaveCoordinator(
                pageStore,
                AutosaveQuietPeriod,
                _time,
                _metrics);
            _autosave.StateChanged += OnAutosaveStateChanged;
        }

        Document = new InkDocument();
        _taffy = new PageTaffyController(
            _pageSession,
            Document,
            _synthesizer,
            _time,
            _metrics);
        Document.Changed += (_, _) => OnDocumentChanged();

        // The debounce clock is injectable so the live-recognition timing — including the 5.3 drag-cancel
        // re-signal — is proven on fake time, exactly as Penumbra.Core's DebouncerTests do. Production
        // passes nothing and gets the system clock.
        _liveDebouncer = new Debouncer(
            LiveQuietPeriod,
            OnLiveQuietPeriodElapsed,
            _time);
    }

    /// <summary>Test seam for observing worker completion without owning Avalonia's global dispatcher.</summary>
    internal MainWindowViewModel(
        IRegionRecognizer regionRecognizer,
        SheetGraph sheet,
        TimeProvider time,
        IPageStore pageStore,
        string recoveryPath,
        Action<Action> dispatchPersistenceState)
        : this(
            regionRecognizer,
            sheet,
            time: time,
            pageStore: pageStore,
            recoveryPath: recoveryPath)
    {
        ArgumentNullException.ThrowIfNull(dispatchPersistenceState);
        _dispatchPersistenceState = dispatchPersistenceState;
    }

    public InkDocument Document { get; }

    /// <summary>Read-only graph exposure for diagnostics and persistence; mutations remain transactional here.</summary>
    public IReadOnlyCollection<SheetNode> SheetNodes => _pageSession.SheetNodes;

    /// <summary>Phase 6: the graph panel's headless state — curves detected from the accepted page.</summary>
    public GraphPanelViewModel GraphPanel { get; }

    [ObservableProperty]
    private string _recognitionText = IdleHint;

    /// <summary>Visible local save/recovery state; never sent off-device.</summary>
    [ObservableProperty]
    private string _persistenceStatus = string.Empty;

    /// <summary>Visible health of the crash-recovery shadow, separate from sticky user-action warnings.</summary>
    [ObservableProperty]
    private string _recoveryCheckpointStatus = string.Empty;

    /// <summary>The last explicitly saved/opened local page path, if any.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>Whether the current raw-ink revision lacks an exact durable page save.</summary>
    public bool IsDirty => !IsCurrentDocumentDurable;

    /// <summary>Immutable owner-keyed answer overlay, always separate from <see cref="Document"/>.</summary>
    [ObservableProperty]
    private AnswerLayer _answerLayer = AnswerLayer.Empty;

    [ObservableProperty]
    private CausalityRipple? _causalityRipple;

    /// <summary>5.3: current per-line numeric literals published for validated canvas hit-testing.</summary>
    [ObservableProperty]
    private LiteralRunLayer _literalRunLayer = LiteralRunLayer.Empty;

    /// <summary>
    /// Static hypothetical ink for the active taffy gesture, or null outside one. This layer is never
    /// persisted, replayed, banked, recognized, or inserted into <see cref="Document"/>.
    /// </summary>
    [ObservableProperty]
    private TaffyGhostLayer? _taffyGhostLayer;

    internal bool IsTaffyActive => _taffy.IsActive;
    internal int TaffyProbeCount => _taffy.ProbeCount;

    [ObservableProperty]
    private bool _liveRecognition = true;

    /// <summary>Explicit mouse tool; pen eraser/inversion is detected directly by the canvas.</summary>
    [ObservableProperty]
    private bool _isEraseMode;

    [ObservableProperty]
    private IReadOnlySet<Guid> _uncertainStrokeIds = NoStrokes;

    [ObservableProperty]
    private IReadOnlySet<Guid> _provenanceStrokeIds = NoStrokes;

    public void NotifyStrokeStarted()
    {
        CancelLiveRecognitionQuietPeriod();
        CancelRecognition();
    }

    private void SignalLiveRecognition(TimeSpan quietPeriod)
    {
        if (_disposed)
        {
            return;
        }

        long generation = Interlocked.Increment(ref _quietPeriodGeneration);

        // Preserve the default no-op's zero-allocation/zero-timestamp contract all the way through App.
        if (ReferenceEquals(_metrics, NoOpLocalMetricsSink.Instance))
        {
            _liveDebouncer.Signal(quietPeriod, generation);
            return;
        }

        var next = new QuietPeriodMetricState(
            MetricTimingScope.Start(_metrics, MetricOperation.RecognitionQuietPeriod, _time),
            generation);
        QuietPeriodMetricState? superseded = Interlocked.Exchange(ref _quietPeriodMetric, next);
        superseded?.Scope.Cancel();
        try
        {
            _liveDebouncer.Signal(quietPeriod, generation);
        }
        catch
        {
            Interlocked.CompareExchange(ref _quietPeriodMetric, null, next);
            next.Scope.Fail();
            throw;
        }
    }

    private void CancelLiveRecognitionQuietPeriod()
    {
        Interlocked.Increment(ref _quietPeriodGeneration);
        _liveDebouncer.Cancel();
        Interlocked.Exchange(ref _quietPeriodMetric, null)?.Scope.Cancel();
    }

    private void OnLiveQuietPeriodElapsed(long generation)
    {
        // Timer authority and diagnostics are deliberately separate. A replacement signal may arrive after
        // this callback posts but before the UI applies it; the generation check suppresses that stale work.
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == Volatile.Read(ref _quietPeriodGeneration))
            {
                _ = RecognizeCoreAsync(RecognitionMode.Live);
            }
        });

        if (!ReferenceEquals(_metrics, NoOpLocalMetricsSink.Instance))
        {
            QuietPeriodMetricState? current = Volatile.Read(ref _quietPeriodMetric);
            if (current is null || current.Generation != generation)
            {
                return;
            }

            if (!ReferenceEquals(
                    Interlocked.CompareExchange(ref _quietPeriodMetric, null, current),
                    current))
            {
                return;
            }

            current.Scope.Complete();
        }
    }

    /// <summary>Toggles provenance for one answer owner; overlapping answers never borrow another line's tokens.</summary>
    public void ToggleAnswerProvenance(Guid ownerId)
    {
        if (ProvenanceStrokeIds.Count > 0)
        {
            ProvenanceStrokeIds = NoStrokes;
            return;
        }

        SheetNode? node = _sheet.Find(ownerId);
        ProvenanceStrokeIds = node is null
            ? NoStrokes
            : node.Tokens.SelectMany(token => token.SourceStrokeIds).ToHashSet();
    }

    /// <summary>
    /// 5.3 A1 (D9): stamps a dragged answer into the document as real ink — the one and only path by which
    /// answer ink enters <see cref="Document"/>. Copies the owner's already-synthesized strokes (never
    /// re-synthesizing), translates them by the drag delta so they land where the ghost showed, and — when
    /// the drop y-overlaps an existing line — rescales them about their own centre to match that line's
    /// glyph height. A direct drop on a recognized literal atomically replaces that literal's source ink;
    /// other safe drops append. Both are ONE undoable edit. A far-horizontal drop in an existing line's
    /// Y-band is refused because today's one-expression-per-line segmenter would merge it despite the visual
    /// whitespace. Every fresh id is recorded so re-inked values are never banked (D10). The resulting
    /// <see cref="InkDocument.Changed"/> drives normal recognition; this method never writes the graph.
    /// </summary>
    public void StampAnswer(Guid ownerId, double dx, double dy, double dropX, double dropY)
    {
        AnswerAnimation? answer = AnswerLayer.Answers.FirstOrDefault(a => a.OwnerId == ownerId);
        IReadOnlyList<Stroke>? source = answer?.Handwriting.Strokes;
        PageStampResult result = PageStampTransaction.Apply(
            Document,
            _pageSession.AcceptedRegions,
            ownerId,
            source,
            dx,
            dy,
            dropX,
            dropY);
        if (result.Refusal == PageStampRefusal.UnsafeHorizontalDrop)
        {
            RecognitionText = "That space belongs to an existing line — move vertically to create a new line.";
            NotifyAnswerDragCancelled();
            return;
        }

        if (result.HideSourceAnswer)
        {
            // The committed answer and its real-ink copy would otherwise overlap until the debounced read
            // reclassifies the source line as a statement. Hide it immediately; recognition may restore it
            // if the resulting line remains a query.
            AnswerLayer = new AnswerLayer(AnswerLayer.Answers.Where(item => item.OwnerId != ownerId).ToArray());
        }
    }

    /// <summary>
    /// 5.3 A1 (D8): call when an answer drag was abandoned without stamping (dropped back within the start
    /// slop, or Escape). The grab's pen-down already cancelled any pending live read, and a cancelled drag
    /// mutates nothing, so nothing else would restart it — re-signal the debouncer to restore that eaten
    /// pass. A completed stamp needs no such call: its <see cref="InkDocument.Changed"/> re-signals naturally.
    /// </summary>
    public void NotifyAnswerDragCancelled()
    {
        if (Document.Strokes.Count > 0 && LiveRecognition)
        {
            SignalLiveRecognition(LiveQuietPeriod);
        }
    }

    /// <summary>
    /// Starts a non-mutating taffy session for a literal from the current <see cref="LiteralRunLayer"/>.
    /// The snapshot is accepted only while its owner, token span, value, and every source stroke still match
    /// the committed page. On success the original literal is muted and a lifted static copy is shown; no
    /// graph probe occurs until horizontal motion crosses a snapped value boundary.
    /// </summary>
    public bool BeginTaffy(Guid ownerId, LiteralRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        EndTaffyCore(resignalRecognition: false);
        bool accepted = _taffy.Begin(
            ownerId,
            run,
            AnswerLayer.Answers.Select(answer => answer.OwnerId).ToHashSet());
        if (accepted)
        {
            ApplyTaffyFrame(_taffy.CurrentFrame);
        }
        return accepted;
    }

    /// <summary>
    /// Updates the active taffy trial from cumulative screen-space horizontal motion. Mapping always starts
    /// from the originally recognized literal, probes only when the snapped value changes, and enforces a
    /// 33 ms floor between probes. The Sheet probe and all synthesized ghosts are scratch presentation state;
    /// committed nodes, recognition round-trip state, answers, document ink, and undo history stay untouched.
    /// </summary>
    public void UpdateTaffy(double screenDx)
    {
        PageTaffyUpdateResult result = _taffy.Update(screenDx);
        if (result.Probed)
        {
            ApplyTaffyFrame(result.Frame);
        }
    }

    /// <summary>
    /// Ends taffy without committing its hypothetical state. The presentation layer and per-gesture cache
    /// are discarded, then the normal live debounce is re-signalled because the original pen-down cancelled
    /// a pending read even though the gesture never changed the document.
    /// </summary>
    public void EndTaffy() => EndTaffyCore(resignalRecognition: true);

    private void EndTaffyCore(bool resignalRecognition)
    {
        bool wasActive = _taffy.End();
        TaffyGhostLayer = null;
        if (wasActive && resignalRecognition)
        {
            NotifyAnswerDragCancelled();
        }
    }

    private void ApplyTaffyFrame(PageTaffyFrame? frame)
    {
        if (frame is null)
        {
            TaffyGhostLayer = null;
            return;
        }

        TaffyGhost[] ghosts = frame.Ghosts.Select(ghost => new TaffyGhost(
            ghost.OwnerId,
            ghost.ValueText,
            ghost.Handwriting,
            ghost.IsLiteral,
            ghost.LiftScreenPixels)).ToArray();
        TaffyGhostLayer = new TaffyGhostLayer(
            frame.MutedStrokeIds,
            frame.HiddenAnswerOwnerIds,
            ghosts,
            ++_visualSequence);
    }

    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private Task Recognize() => RecognizeCoreAsync(RecognitionMode.Manual);

    /// <summary>Headless-friendly explicit read used by tests and non-command hosts.</summary>
    public Task RecognizeNowAsync(bool manual = false) =>
        RecognizeCoreAsync(manual ? RecognitionMode.Manual : RecognitionMode.Live);

    private async Task RecognizeCoreAsync(RecognitionMode mode)
    {
        if (_disposed)
        {
            return;
        }

        CancelRecognition();
        var cts = new CancellationTokenSource();
        _recognitionCts = cts;
        long generation = ++_recognitionGeneration;

        // PageRecognitionSession snapshots both raw ink and its last committed recognition cache before
        // work leaves the UI thread. Pointer input can therefore never mutate an in-flight pass.
        PageRecognitionCandidate candidate;
        try
        {
            candidate = await _pageSession.RecognizeAsync(Document.Strokes, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // CancellationToken alone is insufficient when a fake/non-cooperative recognizer returns late.
        // The generation check mechanically enforces latest-pass-wins before any cache/graph/UI mutation.
        if (cts.IsCancellationRequested || generation != _recognitionGeneration || !ReferenceEquals(cts, _recognitionCts))
        {
            return;
        }

        ApplyRegions(candidate, mode);
    }

    private void ApplyRegions(
        PageRecognitionCandidate candidate,
        RecognitionMode mode)
    {
        // This is the shared product transaction used by headless corpus execution too: gate every line,
        // evict absent/rejected nodes, upsert clean moved lines, then recompute Sheet exactly once.
        PageRecognitionApplication application = _pageSession.Apply(candidate);
        IReadOnlyList<RegionRecognition> regions = application.Regions;
        IReadOnlyList<RegionRecognition> acceptedRegions = application.AcceptedRegions;
        Dictionary<Guid, RegionRecognition> accepted = acceptedRegions.ToDictionary(
            region => region.Region.Id);

        // 5.3: snapshot accepted lines as stamp targets and publish the literal boxes taffy will validate.
        PublishLiteralRunLayer(acceptedRegions);

        // Phase 6: the same accepted set drives graph detection — a line that stops being accepted drops its
        // curve on this very pass, and ordinary non-graph math simply never appears in the panel.
        GraphPanel.UpdateFromAcceptedRegions(acceptedRegions);

        UncertainStrokeIds = application.UncertainStrokeIds;
        ProvenanceStrokeIds = NoStrokes;

        UpdateAnswers(application.RecomputeReport, mode);
        UpdateRipple(application.RecomputeReport, application.DirtySourceRegionIds, mode);
        RecognitionText = BuildRecognitionText(regions, accepted);

        // Only after every graph/UI mutation has succeeded does this pass become next-pass cache authority.
        // Glyph banking below is a passive side effect, not part of recognition/Sheet correctness.
        _pageSession.Commit(application);
        if (mode != RecognitionMode.Load)
        {
            BankNewCompletedQueries(regions, application.StrokeSnapshot);
        }
    }

    // 5.3 A1: publishes one grabbable-literals owner per accepted line that has any numeric literal.
    private void PublishLiteralRunLayer(IReadOnlyList<RegionRecognition> acceptedRegions)
    {
        var owners = new List<LiteralRunOwner>(acceptedRegions.Count);
        foreach (RegionRecognition region in acceptedRegions)
        {
            IReadOnlyList<LiteralRun> runs = TaffyLiteralTree.Discover(region.Result)
                .Select(candidate => candidate.Run)
                .ToArray();
            if (runs.Count > 0)
            {
                owners.Add(new LiteralRunOwner(region.Region.Id, runs));
            }
        }

        LiteralRunLayer = new LiteralRunLayer(owners, ++_visualSequence);
    }

    // 5.3 A1 stale-run guard: after any document edit, drop runs whose source strokes no longer all exist,
    // so an erased/rewritten line can never stay grabbable at a stale value. Republished only when the edit
    // actually invalidated a run, keeping the sequence meaningful.
    private void PruneLiteralRunLayer()
    {
        if (LiteralRunLayer.RunCount == 0)
        {
            return;
        }

        HashSet<Guid> present = Document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        LiteralRunLayer pruned = LiteralRunLayer.PruneMissing(present, _visualSequence + 1);
        if (pruned.RunCount != LiteralRunLayer.RunCount)
        {
            _visualSequence++;
            LiteralRunLayer = pruned;
        }
    }

    private void UpdateAnswers(RecomputeReport report, RecognitionMode mode)
    {
        Dictionary<Guid, AnswerAnimation> current = AnswerLayer.Answers.ToDictionary(answer => answer.OwnerId);
        HashSet<Guid> changed = report.ChangedResultNodes.Select(node => node.Id).ToHashSet();
        HashSet<Guid> valid = _sheet.Nodes
            .Where(IsAnswerableQuery)
            .Select(node => node.Id)
            .ToHashSet();

        foreach (Guid stale in current.Keys.Where(id => !valid.Contains(id)).ToArray())
        {
            current.Remove(stale);
        }

        foreach (SheetNode node in _sheet.Nodes.Where(IsAnswerableQuery))
        {
            bool rebuild = mode == RecognitionMode.Manual || mode == RecognitionMode.Load
                || changed.Contains(node.Id) || !current.ContainsKey(node.Id);
            if (!rebuild)
            {
                continue; // same-result recompute keeps the existing answer and does not replay it
            }

            AnswerAnimation? replacement = TryBuildAnimation(
                node.Id,
                node.Result!.DisplayText,
                node.Tokens,
                play: mode != RecognitionMode.Load);
            if (replacement is null)
            {
                // A changed query must not retain a stale visual merely because synthesis now fails.
                current.Remove(node.Id);
            }
            else
            {
                current[node.Id] = replacement;
            }
        }

        AnswerLayer = new AnswerLayer(current.Values.OrderBy(answer => answer.Sequence).ToArray());
    }

    private static bool IsAnswerableQuery(SheetNode node) =>
        PageAnswerMaterializer.IsAnswerableQuery(node);

    private static bool IsUsefulQueryResult(SheetNode node, EvaluationResult result) =>
        PageAnswerMaterializer.IsUsefulQueryResult(node, result);

    private void UpdateRipple(RecomputeReport report, IReadOnlySet<Guid> dirtySources, RecognitionMode mode)
    {
        if (mode != RecognitionMode.Live)
        {
            CausalityRipple = null;
            return;
        }

        CausalityRippleStep[] steps = report.CausallyAffectedNodes
            .Where(node => !dirtySources.Contains(node.Id))
            .Select(node => new CausalityRippleStep(
                node.Id,
                node.Tokens.SelectMany(token => token.SourceStrokeIds).Distinct().ToArray()))
            .Where(step => step.StrokeIds.Count > 0)
            .ToArray();
        CausalityRipple = steps.Length == 0 ? null : new CausalityRipple(steps, ++_visualSequence);
    }

    private void BankNewCompletedQueries(
        IReadOnlyList<RegionRecognition> regions,
        IReadOnlyList<Stroke> strokes)
    {
        if (_glyphBank is null)
        {
            return;
        }

        foreach (RegionRecognition region in regions.Where(region => region.Dirty))
        {
            SheetNode? node = _sheet.Find(region.Region.Id);
            if (node is null || !IsAnswerableQuery(node))
            {
                continue;
            }

            foreach (GlyphSample sample in GlyphCapture.Collect(
                region.Result.Tokens,
                strokes,
                _calibration.BankConfidence,
                DateTimeOffset.UtcNow,
                _bankedStrokeSets))
            {
                // Only durable UserInk is admissible. Synthesized answers remain excluded after reopen;
                // legacy, missing, unknown, or duplicate provenance refuses conservatively.
                if (sample.Strokes.Any(stroke =>
                    Document.GetStrokeOrigin(stroke.Id) != StrokeOriginKind.UserInk))
                {
                    continue;
                }

                _glyphBank.Capture(sample);
            }
        }
    }

    private string BuildRecognitionText(
        IReadOnlyList<RegionRecognition> regions,
        IReadOnlyDictionary<Guid, RegionRecognition> accepted)
    {
        if (regions.Count == 0)
        {
            return IdleHint;
        }

        var lines = new List<string>(regions.Count);
        foreach (RegionRecognition region in regions.OrderBy(region => region.Region.Bounds.Y))
        {
            if (!accepted.ContainsKey(region.Region.Id))
            {
                RecognitionGate.GateResult gate = RecognitionGate.Evaluate(region.Result, _calibration.MinConfidence);
                lines.Add(string.IsNullOrWhiteSpace(region.Result.Latex)
                    ? "(couldn't read that — try clearer symbols)"
                    : gate.Refusal ?? "(couldn't read that — try clearer symbols)");
                continue;
            }

            SheetNode? node = _sheet.Find(region.Region.Id);
            lines.Add(node is { Role: NodeRole.Query, Result: { } result }
                ? result.IsComputed
                    ? $"{node.Latex}  {result.DisplayText}"
                    : $"{node.Latex}  (couldn't compute: {result.DisplayText})"
                : $"read:  {region.Result.Latex}  ({region.Result.Confidence:P0} confident)");
        }

        return string.Join("    ·    ", lines);
    }

    private AnswerAnimation? TryBuildAnimation(
        Guid ownerId,
        string answerText,
        IReadOnlyList<RecognizedToken> tokens,
        bool play)
    {
        SynthesizedHandwriting? synthesized = PageAnswerMaterializer.TrySynthesize(
            answerText,
            tokens,
            _synthesizer);
        return synthesized is null
            ? null
            : new AnswerAnimation(ownerId, synthesized, ++_visualSequence, play);
    }

    /// <summary>
    /// A line's representative glyph height: the clamped median height of its non-<c>=</c> tokens (the
    /// <c>=</c> is excluded because its bounds don't track the digits' size). Clamped to [24, 96] and
    /// defaulting to 48 for a line with no sizeable tokens. Shared by answer spawning and 5.3 stamp
    /// rescaling so a stamped answer matches the line it lands on the same way a fresh answer is sized.
    /// </summary>
    internal static double ClampedMedianTokenHeight(IReadOnlyList<RecognizedToken> tokens) =>
        PageAnswerMaterializer.ClampedMedianTokenHeight(tokens);

    /// <summary>
    /// Builds schema v4 from raw ink, durable stroke provenance, and neutral cache snapshots. No graph
    /// edges are serialized; the recognition fingerprint makes every cache hint explicitly revocable.
    /// </summary>
    public PenumbraDocument CreateDocumentSnapshot() =>
        PageDocumentSnapshot.Create(Document, _pageSession);

    /// <summary>Durably saves the current UI-thread snapshot to a local page path.</summary>
    public async Task SavePageAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IPageStore pageStore = _pageStore
            ?? throw new InvalidOperationException("Page persistence is not configured.");
        string fullPath = Path.GetFullPath(path);
        RejectReservedRecoveryPath(fullPath);
        if (!_pageOperationGate.Wait(0))
        {
            PersistenceStatus = "Another page operation is still running; Save was not started.";
            throw new InvalidOperationException("Page save/open operations cannot overlap.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long documentRevision = Volatile.Read(ref _documentRevision);
            PenumbraDocument snapshot = CreateDocumentSnapshot();
            long generation = Interlocked.Increment(ref s_explicitSaveGeneration);
            PageSaveResult result = await Task.Run(
                () => pageStore.SaveAsync(
                    snapshot,
                    fullPath,
                    generation,
                    PageSaveKind.Explicit,
                    cancellationToken),
                cancellationToken);
            if (result.Status != PageSaveStatus.Committed)
            {
                PersistenceStatus = "A newer save superseded this request.";
                return;
            }

            if (result.Generation != generation)
            {
                throw new IOException(
                    $"Page store committed generation {result.Generation} for request {generation}.");
            }

            CurrentPath = fullPath;
            if (Volatile.Read(ref _documentRevision) == documentRevision)
            {
                Volatile.Write(ref _durablySavedDocumentRevision, documentRevision);
                OnPropertyChanged(nameof(IsDirty));
                PersistenceStatus = $"Saved {Path.GetFileName(fullPath)} safely.";
            }
            else
            {
                PersistenceStatus =
                    $"Saved {Path.GetFileName(fullPath)}, but newer ink still needs another Save.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceStatus = "Save cancelled; the previous page remains intact.";
            throw;
        }
        catch
        {
            PersistenceStatus = "Save failed; the previous page remains intact.";
            throw;
        }
        finally
        {
            _pageOperationGate.Release();
        }
    }

    /// <summary>
    /// Opens the validated current page, or deterministically loads its explicit last-known-good candidate
    /// without silently promoting that candidate on disk.
    /// </summary>
    public async Task<PageOpenResult> OpenPageAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IPageStore pageStore = _pageStore
            ?? throw new InvalidOperationException("Page persistence is not configured.");
        string fullPath = Path.GetFullPath(path);
        RejectReservedRecoveryPath(fullPath);
        if (IsDirty)
        {
            PersistenceStatus = "Save the current page before opening another page.";
            throw new InvalidOperationException("Opening another page would displace unsaved ink.");
        }

        if (!_pageOperationGate.Wait(0))
        {
            PersistenceStatus = "Another page operation is still running; Open was not started.";
            throw new InvalidOperationException("Page save/open operations cannot overlap.");
        }

        bool validatedRawLoadStarted = false;
        PageOpenStatus? rawSource = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageOpenResult result = await Task.Run(
                () => pageStore.OpenAsync(fullPath, cancellationToken),
                cancellationToken);
            if (result.Document is not null
                && result.Status is PageOpenStatus.Current or PageOpenStatus.BackupRecoveryCandidate)
            {
                validatedRawLoadStarted = true;
                rawSource = result.Status;
                await LoadDocumentAsync(result.Document);
                CurrentPath = fullPath;
                if (result.Status == PageOpenStatus.Current)
                {
                    MarkCurrentDocumentDurable();
                }

                ScheduleRecoverySnapshot();
                PersistenceStatus = result.Status == PageOpenStatus.Current
                    ? $"Opened {Path.GetFileName(fullPath)}."
                    : "Loaded the last-known-good copy; Save explicitly to replace the damaged page.";
            }
            else
            {
                PersistenceStatus = result.Status == PageOpenStatus.NotFound
                    ? "That page no longer exists."
                    : "The page and its recovery copy are both unreadable; nothing was opened.";
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceStatus = "Open cancelled.";
            throw;
        }
        catch
        {
            if (validatedRawLoadStarted)
            {
                CurrentPath = fullPath;
                if (rawSource == PageOpenStatus.Current)
                {
                    MarkCurrentDocumentDurable();
                }

                ScheduleRecoverySnapshot();
                PersistenceStatus = "Validated raw ink opened, but recognition failed; the ink is preserved.";
            }
            else
            {
                PersistenceStatus = "Open failed; the current canvas was left unchanged.";
            }

            throw;
        }
        finally
        {
            _pageOperationGate.Release();
        }
    }

    /// <summary>
    /// Deterministically restores a validated interrupted-session checkpoint at startup. The recovered
    /// page remains unsaved (<see cref="CurrentPath"/> is null) until the user chooses a destination.
    /// </summary>
    public async Task<PageOpenResult?> RecoverInterruptedSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_pageStore is null || _recoveryPath is null)
        {
            return null;
        }

        if (!_pageOperationGate.Wait(0))
        {
            PersistenceStatus = "Another page operation is still running; startup recovery was not started.";
            throw new InvalidOperationException("Page save/open operations cannot overlap.");
        }

        bool validatedRawLoadStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageOpenResult result = await Task.Run(
                () => _pageStore.OpenAsync(_recoveryPath, cancellationToken),
                cancellationToken);
            Volatile.Write(ref _recoveryInspectionCompleted, 1);
            if (result.Document is not null
                && result.Status is PageOpenStatus.Current or PageOpenStatus.BackupRecoveryCandidate)
            {
                validatedRawLoadStarted = true;
                await LoadDocumentAsync(result.Document);
                CurrentPath = null;
                ScheduleRecoverySnapshot();
                PersistenceStatus = result.Status == PageOpenStatus.Current
                    ? "Recovered the interrupted local session. Choose Save to keep it."
                    : "Recovered the last-known-good interrupted session. Choose Save to keep it.";
            }
            else if (result.Status == PageOpenStatus.Unrecoverable)
            {
                PersistenceStatus = "Interrupted-session recovery data is corrupt; the blank page was kept.";
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PersistenceStatus = "Startup recovery cancelled.";
            throw;
        }
        catch
        {
            if (validatedRawLoadStarted)
            {
                CurrentPath = null;
                ScheduleRecoverySnapshot();
                PersistenceStatus =
                    "Recovered validated raw ink, but recognition failed; choose Save to keep the ink.";
            }
            else
            {
                PersistenceStatus = "Startup recovery failed; the current canvas was left unchanged.";
            }

            throw;
        }
        finally
        {
            _pageOperationGate.Release();
        }
    }

    /// <summary>Flushes the latest recovery revision, then removes the checkpoint on a clean close.</summary>
    public async Task CompleteCleanShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_autosave is null || _recoveryPath is null)
        {
            return;
        }

        bool pageOperationHeld = false;
        try
        {
            await _pageOperationGate.WaitAsync(cancellationToken);
            pageOperationHeld = true;

            if (IsDirty)
            {
                // Document edits normally schedule synchronously, but direct validated loads and any
                // future owner-thread mutation path must still get one exact final checkpoint.
                _autosave.Schedule(CreateDocumentSnapshot(), _recoveryPath);
                RecoveryCheckpointStatus = "Final local recovery checkpoint pending.";
            }

            Interlocked.Exchange(ref _cleanShutdownInProgress, 1);

            await Task.Run(
                () => _autosave.FlushAsync(cancellationToken),
                cancellationToken);

            if (Volatile.Read(ref _recoveryInspectionCompleted) == 0
                && _autosave.LatestRevision == 0)
            {
                RecoveryCheckpointStatus =
                    "Unverified recovery data was kept during this clean close.";
                return;
            }

            if (IsDirty)
            {
                RecoveryCheckpointStatus =
                    "Unsaved page retained in local recovery. Choose Save after reopening it.";
                return;
            }

            await Task.Run(
                () =>
                {
                    // Delete the older backup first. If either deletion fails, the newest validated
                    // checkpoint remains at the authoritative current path for a retry/recovery.
                    File.Delete(FileSystemPageStore.GetBackupPath(_recoveryPath));
                    File.Delete(_recoveryPath);
                },
                cancellationToken);
            RecoveryCheckpointStatus = "Local recovery checkpoint cleared after a clean close.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _cleanShutdownInProgress, 0);
            PersistenceStatus = "Close flush cancelled; recovery data was kept.";
            if (pageOperationHeld)
            {
                ScheduleRecoverySnapshot();
            }
            throw;
        }
        catch
        {
            Volatile.Write(ref _cleanShutdownInProgress, 0);
            PersistenceStatus = IsCurrentDocumentDurable
                ? "Close flush failed; recovery data was kept, the saved page is safe, and the window remains open."
                : "Close flush failed; recovery data was kept and the window remains open.";
            if (pageOperationHeld)
            {
                ScheduleRecoverySnapshot();
            }
            throw;
        }
        finally
        {
            if (pageOperationHeld)
            {
                _pageOperationGate.Release();
            }
        }
    }

    /// <summary>Lets the view surface storage-provider failures without coupling Core to Avalonia.</summary>
    public void ReportPersistenceFailure(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        PersistenceStatus = message;
    }

    /// <summary>
    /// Loads raw ink first, then treats structurally trusted v4 snapshots from the exact current recognition
    /// pipeline only as cache input. Sheet edges, roles, conflicts and results are always rebuilt through
    /// segmentation + Upsert + recompute; persisted results are never authoritative. Legacy, mismatched, or
    /// hostile cache metadata takes the same fresh-recognition path without displacing raw ink.
    /// </summary>
    public async Task LoadDocumentAsync(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EndTaffyCore(resignalRecognition: false);
        CancelLiveRecognitionQuietPeriod();
        CancelRecognition();
        ++_recognitionGeneration;
        _suppressDocumentChanged = true;
        try
        {
            Document.Load(document);
        }
        finally
        {
            _suppressDocumentChanged = false;
        }

        Interlocked.Increment(ref _documentRevision);
        OnPropertyChanged(nameof(IsDirty));

        _bankedStrokeSets.Clear();
        _pageSession.Clear();
        _pageSession.ReplaceCache(PageRecognitionCache.BuildValidLoadCache(document));
        AnswerLayer = AnswerLayer.Empty;
        LiteralRunLayer = LiteralRunLayer.Empty;
        GraphPanel.Clear();
        CausalityRipple = null;
        ProvenanceStrokeIds = NoStrokes;
        await RecognizeCoreAsync(RecognitionMode.Load);
    }

    private bool CanRecognize => Document.Strokes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Document.Undo();
    private bool CanUndo => Document.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Document.Redo();
    private bool CanRedo => Document.CanRedo;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear() => Document.Clear();
    private bool CanClear => Document.Strokes.Count > 0;

    partial void OnLiveRecognitionChanged(bool value)
    {
        if (!value) CancelLiveRecognitionQuietPeriod();
        else if (Document.Strokes.Count > 0) SignalLiveRecognition(LiveQuietPeriod);
    }

    private void OnDocumentChanged()
    {
        int previousStrokeCount = _lastKnownStrokeCount;
        _lastKnownStrokeCount = Document.Strokes.Count;

        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RecognizeCommand.NotifyCanExecuteChanged();
        if (_suppressDocumentChanged) return;
        Interlocked.Increment(ref _documentRevision);
        OnPropertyChanged(nameof(IsDirty));
        if (_autosave is not null)
        {
            PersistenceStatus =
                "Unsaved changes are protected by local recovery; choose Save for a page file.";
        }

        // A taffy frame describes the exact committed stroke/token snapshot at grab time. Any document edit
        // invalidates it immediately; the edit's own debounce replaces the no-op end re-signal.
        EndTaffyCore(resignalRecognition: false);

        // Undo/redo, clear, load-adjacent hosts, and programmatic InkDocument edits do not pass through
        // NotifyStrokeStarted. They still invalidate an in-flight snapshot immediately: allowing that
        // stale pass to apply until the next debounce would briefly restore erased values/answers.
        CancelRecognition();
        ++_recognitionGeneration;
        ProvenanceStrokeIds = NoStrokes;
        if (Document.Strokes.Count == 0)
        {
            ResetTransientState();
        }
        else
        {
            // 5.3 A1: keep the grabbable-literals layer honest between recognition passes — a stroke that
            // just vanished must not leave its line grabbable at a stale value.
            PruneLiteralRunLayer();
            if (LiveRecognition)
            {
                SignalLiveRecognition(QuietPeriodFor(previousStrokeCount, Document.Strokes.Count));
            }
        }

        ScheduleRecoverySnapshot();
    }

    private void ScheduleRecoverySnapshot()
    {
        if (_autosave is null
            || _recoveryPath is null
            || _disposed
            || Volatile.Read(ref _cleanShutdownInProgress) != 0)
        {
            return;
        }

        try
        {
            // Called synchronously from the document-owning thread: the coordinator receives only an
            // immutable snapshot and never reaches back into InkDocument from its timer/I/O work.
            _autosave.Schedule(CreateDocumentSnapshot(), _recoveryPath);
            RecoveryCheckpointStatus = "Local recovery checkpoint pending.";
        }
        catch
        {
            RecoveryCheckpointStatus =
                "Could not schedule local recovery; explicit Save is still available.";
        }
    }

    private void OnAutosaveStateChanged(object? sender, PageAutosaveStateChangedEventArgs args)
    {
        void ApplyLatestState()
        {
            if (_disposed
                || Volatile.Read(ref _cleanShutdownInProgress) != 0
                || _autosave is null
                || args.Revision != _autosave.LatestRevision)
            {
                return;
            }

            RecoveryCheckpointStatus = args.Committed
                ? "Local recovery checkpoint saved."
                : "Local recovery checkpoint failed; explicit Save is still available.";
        }

        _dispatchPersistenceState(ApplyLatestState);
    }

    private void MarkCurrentDocumentDurable()
    {
        Volatile.Write(
            ref _durablySavedDocumentRevision,
            Volatile.Read(ref _documentRevision));
        OnPropertyChanged(nameof(IsDirty));
    }

    private bool IsCurrentDocumentDurable =>
        Volatile.Read(ref _durablySavedDocumentRevision) == Volatile.Read(ref _documentRevision);

    private void RejectReservedRecoveryPath(string fullPath)
    {
        if (_recoveryPath is null)
        {
            return;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(fullPath, _recoveryPath, comparison)
            || string.Equals(fullPath, FileSystemPageStore.GetBackupPath(_recoveryPath), comparison))
        {
            PersistenceStatus =
                "The internal recovery checkpoint cannot be used as an explicit page path.";
            throw new InvalidOperationException("The requested path is reserved for session recovery.");
        }
    }

    private static string DefaultRecoveryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Penumbra",
        "Recovery",
        "interrupted-session.pen");

    /// <summary>
    /// The debounce window for a document change: stroke-removing edits (erase, undo of an add) wait
    /// the longer erase grace because a rewrite usually follows; everything else uses the live period.
    /// </summary>
    internal static TimeSpan QuietPeriodFor(int previousStrokeCount, int currentStrokeCount) =>
        currentStrokeCount < previousStrokeCount ? EraseQuietPeriod : LiveQuietPeriod;

    private void ResetTransientState()
    {
        CancelLiveRecognitionQuietPeriod();
        CancelRecognition();
        ++_recognitionGeneration;
        _pageSession.Clear();
        AnswerLayer = AnswerLayer.Empty;
        LiteralRunLayer = LiteralRunLayer.Empty;
        GraphPanel.Clear();
        EndTaffyCore(resignalRecognition: false);
        CausalityRipple = null;
        UncertainStrokeIds = NoStrokes;
        ProvenanceStrokeIds = NoStrokes;
        _bankedStrokeSets.Clear();
        RecognitionText = IdleHint;
    }

    private void CancelRecognition()
    {
        _recognitionCts?.Cancel();
        _recognitionCts?.Dispose();
        _recognitionCts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelLiveRecognitionQuietPeriod();
        _liveDebouncer.Dispose();
        EndTaffyCore(resignalRecognition: false);
        CancelRecognition();
        if (_autosave is not null)
        {
            _autosave.StateChanged -= OnAutosaveStateChanged;
        }
        _autosave?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private enum RecognitionMode { Live, Manual, Load }

    private sealed record QuietPeriodMetricState(
        MetricTimingScope Scope,
        long Generation);

    private sealed class EmptyRegionRecognizer : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Array.Empty<RegionRecognition>();

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Task.FromResult(RecognizeRegions(strokes, previous, cancellationToken));
    }
}
