using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Penumbra.Cas;
using Penumbra.Cas.Latex;
using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
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
    private const string IdleHint = "Write expressions on separate lines — queries ending in '=' answer when you pause";

    internal static readonly TimeSpan LiveQuietPeriod = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan TaffyProbeFloor = TimeSpan.FromMilliseconds(33);

    // s19 dogfood: an erase is usually the first half of "erase, then rewrite". Recomputing on the
    // normal quiet period races the user — the half-edited line ('x=' with its value gone) recognizes,
    // ripples the whole dependency chain, then everything recomputes AGAIN when the new value lands.
    // Stroke-removing edits therefore get a longer window; pen-down still cancels it instantly.
    internal static readonly TimeSpan EraseQuietPeriod = TimeSpan.FromSeconds(2.2);

    private readonly IRegionRecognizer _regionRecognizer;
    private readonly SheetGraph _sheet;
    private readonly IGlyphBank? _glyphBank;
    private readonly HandwritingSynthesizer? _synthesizer;
    private readonly RecognitionCalibration _calibration;
    private readonly Debouncer _liveDebouncer;
    private readonly TimeProvider _time;
    private readonly HashSet<string> _bankedStrokeSets = new();

    // 5.3 A1: strokes that entered the document as a stamped answer, this session only. They must never
    // feed the glyph bank — a re-inked answer is not fresh handwriting evidence, and banking the
    // synthesizer's own output would drift the corpus (jitter of jitter). Not persisted (accepted A1 limit).
    private readonly HashSet<Guid> _stampedStrokeIds = new();

    // This list is the recognizer's complete round-trip state, including rejected regions. It is replaced
    // only by an atomically applied latest pass; a cancelled/superseded result never becomes cache authority.
    private IReadOnlyList<RegionRecognition> _previousRegions = Array.Empty<RegionRecognition>();

    // 5.3 A1: the accepted regions of the last applied pass — the candidate drop lines a stamped answer can
    // snap to (bounds drive the y-overlap test, tokens the target line height). Kept separate from
    // _previousRegions so recognition-cache semantics are never touched.
    private IReadOnlyList<RegionRecognition> _lastAppliedRegions = Array.Empty<RegionRecognition>();
    private CancellationTokenSource? _recognitionCts;
    private long _recognitionGeneration;
    private long _visualSequence;
    private bool _suppressDocumentChanged;
    private int _lastKnownStrokeCount;
    private bool _disposed;

    // 5.3 A2: one non-mutating taffy session. The cache lives only for that gesture: repeated snapped
    // values reuse byte-identical geometry (no shimmer), while a later gesture revalidates anchors/strokes.
    private TaffySession? _taffySession;
    private readonly Dictionary<TaffyGhostCacheKey, SynthesizedHandwriting?> _taffyGhostCache = new();

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
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(regionRecognizer);
        ArgumentNullException.ThrowIfNull(sheet);

        _regionRecognizer = regionRecognizer;
        _sheet = sheet;
        _glyphBank = glyphBank;
        _synthesizer = synthesizer;
        _calibration = calibration ?? RecognitionCalibration.Default;
        _time = time ?? TimeProvider.System;

        Document = new InkDocument();
        Document.Changed += (_, _) => OnDocumentChanged();

        // The debounce clock is injectable so the live-recognition timing — including the 5.3 drag-cancel
        // re-signal — is proven on fake time, exactly as Penumbra.Core's DebouncerTests do. Production
        // passes nothing and gets the system clock.
        _liveDebouncer = new Debouncer(
            LiveQuietPeriod,
            () => Dispatcher.UIThread.Post(() => _ = RecognizeCoreAsync(RecognitionMode.Live)),
            time);
    }

    public InkDocument Document { get; }

    /// <summary>Read-only graph exposure for diagnostics and persistence; mutations remain transactional here.</summary>
    public IReadOnlyCollection<SheetNode> SheetNodes => _sheet.Nodes;

    [ObservableProperty]
    private string _recognitionText = IdleHint;

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

    internal bool IsTaffyActive => _taffySession is not null;
    internal int TaffyProbeCount { get; private set; }

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
        _liveDebouncer.Cancel();
        CancelRecognition();
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
        if (source is null || source.Count == 0)
        {
            return;
        }

        if (!TryStrokeBounds(source, out double minX, out double minY, out double maxX, out double maxY))
        {
            return;
        }

        double centreX = (minX + maxX) / 2;
        double centreY = (minY + maxY) / 2;

        RegionRecognition? targetRegion = DropTargetRegion(dropY);
        LiteralDropTarget? targetLiteral = LiteralTargetAt(dropX, dropY);

        // Match the target line's glyph height when dropped onto one; otherwise keep the answer's own size.
        double scale = 1.0;
        double sourceHeight = maxY - minY;
        if (sourceHeight > 0 && targetRegion is not null)
        {
            scale = ClampedMedianTokenHeight(targetRegion.Result.Tokens) / sourceHeight;
        }

        IReadOnlyList<Stroke> stamped = StrokeTransformer.Transform(source, dx, dy, scale, centreX, centreY);
        if (targetRegion is not null && targetLiteral is null && !IsNearLine(stamped, targetRegion))
        {
            // Regions are intentionally one expression per horizontal line. Ink far to the side but in the
            // same Y band would still merge into that expression; refuse instead of silently corrupting it.
            RecognitionText = "That space belongs to an existing line — move vertically to create a new line.";
            NotifyAnswerDragCancelled();
            return;
        }

        foreach (Stroke stroke in stamped)
        {
            _stampedStrokeIds.Add(stroke.Id);
        }

        if (targetRegion?.Region.Id == ownerId)
        {
            // The committed answer and its real-ink copy would otherwise overlap until the debounced read
            // reclassifies the source line as a statement. Hide it immediately; recognition may restore it
            // if the resulting line remains a query.
            AnswerLayer = new AnswerLayer(AnswerLayer.Answers.Where(item => item.OwnerId != ownerId).ToArray());
        }

        if (targetLiteral is not null)
        {
            Document.ReplaceStrokes(targetLiteral.Run.SourceStrokeIds, stamped);
        }
        else
        {
            Document.AddStrokes(stamped);
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
            _liveDebouncer.Signal(LiveQuietPeriod);
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

        SheetNode? node = _sheet.Find(ownerId);
        LiteralRun? current = LiteralRunLayer.Owners
            .FirstOrDefault(owner => owner.OwnerId == ownerId)
            ?.Runs.FirstOrDefault(candidate => SameRun(candidate, run));
        if (node is null || current is null
            || current.TokenStart < 0
            || current.TokenStart + current.TokenCount > node.Tokens.Count)
        {
            return false;
        }

        HashSet<Guid> present = Document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        if (current.SourceStrokeIds.Count == 0 || current.SourceStrokeIds.Any(id => !present.Contains(id)))
        {
            return false;
        }

        _taffyGhostCache.Clear();
        TaffyProbeCount = 0;
        DateTimeOffset now = _time.GetUtcNow();
        _taffySession = new TaffySession(
            ownerId,
            current,
            node.Tokens.ToArray(),
            current.ValueText,
            current.ValueText,
            now - TaffyProbeFloor);
        PublishTaffyLayer(_taffySession, current.ValueText, report: null);
        return true;
    }

    /// <summary>
    /// Updates the active taffy trial from cumulative screen-space horizontal motion. Mapping always starts
    /// from the originally recognized literal, probes only when the snapped value changes, and enforces a
    /// 33 ms floor between probes. The Sheet probe and all synthesized ghosts are scratch presentation state;
    /// committed nodes, recognition round-trip state, answers, document ink, and undo history stay untouched.
    /// </summary>
    public void UpdateTaffy(double screenDx)
    {
        TaffySession? session = _taffySession;
        if (session is null)
        {
            return;
        }

        string valueText;
        try
        {
            valueText = TaffyValueMapper.Map(session.OriginalValueText, screenDx);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            return; // An out-of-range recognized literal is honestly non-scrubbable, never an app crash.
        }

        if (string.Equals(valueText, session.LastValueText, StringComparison.Ordinal))
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (now - session.LastProbeAt < TaffyProbeFloor)
        {
            return;
        }

        string trialLatex = LiteralRuns.Splice(session.Tokens, session.Run, valueText);
        SheetProbeReport report = _sheet.Probe(session.OwnerId, trialLatex);
        session.LastValueText = valueText;
        session.LastProbeAt = now;
        TaffyProbeCount++;
        PublishTaffyLayer(session, valueText, report);
    }

    /// <summary>
    /// Ends taffy without committing its hypothetical state. The presentation layer and per-gesture cache
    /// are discarded, then the normal live debounce is re-signalled because the original pen-down cancelled
    /// a pending read even though the gesture never changed the document.
    /// </summary>
    public void EndTaffy() => EndTaffyCore(resignalRecognition: true);

    private void EndTaffyCore(bool resignalRecognition)
    {
        bool wasActive = _taffySession is not null;
        _taffySession = null;
        _taffyGhostCache.Clear();
        TaffyGhostLayer = null;
        if (wasActive && resignalRecognition)
        {
            NotifyAnswerDragCancelled();
        }
    }

    private static bool SameRun(LiteralRun a, LiteralRun b) =>
        a.TokenStart == b.TokenStart
        && a.TokenCount == b.TokenCount
        && string.Equals(a.ValueText, b.ValueText, StringComparison.Ordinal)
        && a.SourceStrokeIds.SequenceEqual(b.SourceStrokeIds);

    // The accepted line the drop landed on (y-overlap of its bounds, padded half a line), or null for empty
    // space. When several overlap, the one whose centre is nearest the drop wins. Tokens drive the height.
    private RegionRecognition? DropTargetRegion(double dropY)
    {
        RegionRecognition? best = null;
        double bestDistance = double.MaxValue;
        foreach (RegionRecognition region in _lastAppliedRegions)
        {
            InkBounds bounds = region.Region.Bounds;
            double pad = bounds.Height * 0.5;
            if (dropY < bounds.Y - pad || dropY > bounds.Y + bounds.Height + pad)
            {
                continue;
            }

            double distance = Math.Abs(dropY - (bounds.Y + bounds.Height / 2));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = region;
            }
        }

        return best;
    }

    // A direct drop on a recognized literal means replacement, not over-drawing. A modest fraction-of-glyph
    // pad absorbs hand placement imprecision without turning adjacent insertion positions into replacements.
    private LiteralDropTarget? LiteralTargetAt(double dropX, double dropY)
    {
        LiteralDropTarget? best = null;
        double bestDistance = double.MaxValue;
        HashSet<Guid> present = Document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        foreach (LiteralRunOwner owner in LiteralRunLayer.Owners)
        {
            foreach (LiteralRun run in owner.Runs)
            {
                InkBounds bounds = run.UnionBounds;
                double pad = Math.Max(8, bounds.Height * 0.35);
                bool inside = dropX >= bounds.X - pad && dropX <= bounds.X + bounds.Width + pad
                    && dropY >= bounds.Y - pad && dropY <= bounds.Y + bounds.Height + pad;
                if (!inside || run.SourceStrokeIds.Count == 0 || run.SourceStrokeIds.Any(id => !present.Contains(id)))
                {
                    continue;
                }

                double dx = dropX - (bounds.X + bounds.Width / 2);
                double dy = dropY - (bounds.Y + bounds.Height / 2);
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new LiteralDropTarget(owner.OwnerId, run);
                }
            }
        }

        return best;
    }

    private static bool IsNearLine(IReadOnlyList<Stroke> stamped, RegionRecognition target)
    {
        if (!TryStrokeBounds(stamped, out double minX, out _, out double maxX, out _))
        {
            return false;
        }

        InkBounds bounds = target.Region.Bounds;
        double gap = maxX < bounds.X
            ? bounds.X - maxX
            : minX > bounds.X + bounds.Width ? minX - (bounds.X + bounds.Width) : 0;
        double lineHeight = ClampedMedianTokenHeight(target.Result.Tokens);
        return gap <= Math.Max(24, lineHeight * 0.75);
    }

    private static bool TryStrokeBounds(
        IReadOnlyList<Stroke> strokes, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;
        bool any = false;
        foreach (Stroke stroke in strokes)
        {
            foreach (StrokeSample sample in stroke.Samples)
            {
                any = true;
                minX = Math.Min(minX, sample.X);
                minY = Math.Min(minY, sample.Y);
                maxX = Math.Max(maxX, sample.X);
                maxY = Math.Max(maxY, sample.Y);
            }
        }

        return any;
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

        // Both collections are immutable snapshots while work runs off-thread. In particular, do not pass
        // InkDocument.Strokes itself: pointer input may mutate it before segmentation finishes.
        Stroke[] strokes = Document.Strokes.ToArray();
        RegionRecognition[] previous = _previousRegions.ToArray();

        IReadOnlyList<RegionRecognition> regions;
        try
        {
            regions = await _regionRecognizer.RecognizeRegionsAsync(strokes, previous, cts.Token);
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

        ApplyRegions(regions, strokes, mode);
    }

    private void ApplyRegions(
        IReadOnlyList<RegionRecognition> regions,
        IReadOnlyList<Stroke> strokeSnapshot,
        RecognitionMode mode)
    {
        var accepted = new Dictionary<Guid, RegionRecognition>();
        var uncertain = new HashSet<Guid>();
        foreach (RegionRecognition region in regions)
        {
            RecognitionGate.GateResult gate = RecognitionGate.Evaluate(region.Result, _calibration.MinConfidence);
            if (!string.IsNullOrWhiteSpace(region.Result.Latex) && gate.Accepted)
            {
                accepted[region.Region.Id] = region;
            }
            else
            {
                uncertain.UnionWith(RecognitionGate.UncertainStrokeIds(region.Result, _calibration.MinConfidence));
            }
        }

        // Apply the whole recognition snapshot as one Sheet transaction: upsert all accepted regions (clean
        // ones included, because Y-position can change definition ownership), remove absent/rejected nodes,
        // then recompute exactly once. Rejected state still round-trips above, but never feeds dependents.
        foreach (Guid id in _sheet.Nodes.Select(node => node.Id).Where(id => !accepted.ContainsKey(id)).ToArray())
        {
            _sheet.Remove(id);
        }

        RegionRecognition[] acceptedRegions = regions.Where(r => accepted.ContainsKey(r.Region.Id)).ToArray();
        foreach (RegionRecognition region in acceptedRegions)
        {
            _sheet.Upsert(region.Region.Id, region.Result.Latex, region.Result.Tokens, region.Region.Bounds);
        }

        // 5.3: snapshot accepted lines as stamp targets and publish the literal boxes taffy will validate.
        _lastAppliedRegions = acceptedRegions;
        PublishLiteralRunLayer(acceptedRegions);

        RecomputeReport report = _sheet.RecomputeDetailed();
        HashSet<Guid> dirtySources = regions.Where(region => region.Dirty).Select(region => region.Region.Id).ToHashSet();

        UncertainStrokeIds = uncertain;
        ProvenanceStrokeIds = NoStrokes;

        UpdateAnswers(report, mode);
        UpdateRipple(report, dirtySources, mode);
        RecognitionText = BuildRecognitionText(regions, accepted);

        // Only after every graph/UI mutation has succeeded does this pass become next-pass cache authority.
        // Glyph banking below is a passive side effect, not part of recognition/Sheet correctness.
        _previousRegions = regions.ToArray();
        if (mode != RecognitionMode.Load)
        {
            BankNewCompletedQueries(regions, strokeSnapshot);
        }
    }

    // 5.3 A1: publishes one grabbable-literals owner per accepted line that has any numeric literal.
    private void PublishLiteralRunLayer(IReadOnlyList<RegionRecognition> acceptedRegions)
    {
        var owners = new List<LiteralRunOwner>(acceptedRegions.Count);
        foreach (RegionRecognition region in acceptedRegions)
        {
            IReadOnlyList<LiteralRun> runs = LiteralRuns.Find(region.Result.Tokens);
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

    private static bool IsAnswerableQuery(SheetNode node)
    {
        return node.Result is { } result && IsUsefulQueryResult(node, result);
    }

    private static bool IsUsefulQueryResult(SheetNode node, EvaluationResult result)
    {
        if (node.Role != NodeRole.Query || node.IsConflict || !result.IsComputed)
        {
            return false;
        }

        if (result.Kind != EvaluationKind.Symbolic)
        {
            return true;
        }

        // An unresolved identity (`y=` → `y`, `y-2=` → `y-2`) is not an answer; drawing it duplicates
        // the user's line and interferes with answer stamping. A real symbolic simplification (`y+y=` →
        // `2y`) still materializes because the translated surfaces differ.
        string queryExpression = node.Latex.TrimEnd();
        if (queryExpression.EndsWith('=') && !queryExpression.EndsWith("==", StringComparison.Ordinal))
        {
            queryExpression = queryExpression[..^1];
        }

        string input = LatexToAngouriMath.Translate(queryExpression);
        string output = LatexToAngouriMath.Translate(result.Latex);
        return !string.Equals(input, output, StringComparison.Ordinal);
    }

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
                // 5.3 A1 (D10): never bank a sample drawn from stamped answer ink — it is the synthesizer's
                // own output re-read, not fresh handwriting, and banking it would drift the corpus.
                if (_stampedStrokeIds.Count > 0 && sample.Strokes.Any(stroke => _stampedStrokeIds.Contains(stroke.Id)))
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
        (InkBounds Anchor, double LineHeight)? spawn = FindSpawn(tokens);
        if (_synthesizer is null || spawn is null)
        {
            return null;
        }

        string handwriting = HandwritingText.FromDisplayText(answerText);
        SynthesizedHandwriting? synthesized = _synthesizer.Synthesize(
            handwriting,
            spawn.Value.Anchor,
            new SynthesisOptions { LineHeight = spawn.Value.LineHeight },
            new Random());
        return synthesized is null || synthesized.MissingSymbols.Count > 0
            ? null
            : new AnswerAnimation(ownerId, synthesized, ++_visualSequence, play);
    }

    // Builds one complete immutable taffy frame. The grabbed literal is always represented (when its
    // glyphs exist); affected query owners hide their committed answers even when the trial errors, so a
    // stale numeric answer can never masquerade as the hypothetical result.
    private void PublishTaffyLayer(TaffySession session, string literalValue, SheetProbeReport? report)
    {
        var ghosts = new List<TaffyGhost>();
        SynthesizedHandwriting? literal = TryBuildTaffyHandwriting(
            new TaffyGhostCacheKey(session.OwnerId, literalValue, IsLiteral: true),
            HandwritingText.FromDisplayText(literalValue),
            LiteralSpawn(session.Run, session.Tokens));
        if (literal is not null)
        {
            ghosts.Add(new TaffyGhost(session.OwnerId, literalValue, literal, IsLiteral: true, LiftScreenPx: 10));
        }

        HashSet<Guid> committedAnswerOwners = AnswerLayer.Answers.Select(answer => answer.OwnerId).ToHashSet();
        var hidden = new HashSet<Guid>();
        if (report is not null)
        {
            foreach (ProbeEntry entry in report.Entries)
            {
                Guid ownerId = entry.Node.Id;
                if (committedAnswerOwners.Contains(ownerId))
                {
                    hidden.Add(ownerId);
                }

                if (!IsUsefulQueryResult(entry.Node, entry.TrialResult))
                {
                    continue;
                }

                (InkBounds Anchor, double LineHeight)? spawn = FindSpawn(entry.Node.Tokens);
                if (spawn is null)
                {
                    continue;
                }

                string valueText = entry.TrialResult.DisplayText;
                SynthesizedHandwriting? answer = TryBuildTaffyHandwriting(
                    new TaffyGhostCacheKey(ownerId, valueText, IsLiteral: false),
                    HandwritingText.FromDisplayText(valueText),
                    spawn.Value);
                if (answer is not null)
                {
                    ghosts.Add(new TaffyGhost(ownerId, valueText, answer, IsLiteral: false));
                }
            }
        }

        TaffyGhostLayer = new TaffyGhostLayer(
            session.Run.SourceStrokeIds.ToHashSet(),
            hidden,
            ghosts,
            ++_visualSequence);
    }

    // Positions the first synthesized glyph at the literal's left edge. The canvas applies the vertical
    // lift in screen pixels so zoom never changes the perceived pickup distance.
    private static (InkBounds Anchor, double LineHeight) LiteralSpawn(
        LiteralRun run,
        IReadOnlyList<RecognizedToken> tokens)
    {
        double lineHeight = ClampedMedianTokenHeight(tokens);
        double gap = new SynthesisOptions().GapAfterAnchor * lineHeight;
        return (new InkBounds(run.UnionBounds.X - gap, run.UnionBounds.Y, 0, run.UnionBounds.Height), lineHeight);
    }

    private SynthesizedHandwriting? TryBuildTaffyHandwriting(
        TaffyGhostCacheKey key,
        string text,
        (InkBounds Anchor, double LineHeight) spawn)
    {
        if (_taffyGhostCache.TryGetValue(key, out SynthesizedHandwriting? cached))
        {
            return cached;
        }

        SynthesizedHandwriting? synthesized = _synthesizer?.Synthesize(
            text,
            spawn.Anchor,
            new SynthesisOptions { LineHeight = spawn.LineHeight },
            new Random(StableTaffySeed(key)));
        if (synthesized is { MissingSymbols.Count: > 0 })
        {
            synthesized = null;
        }

        _taffyGhostCache[key] = synthesized;
        return synthesized;
    }

    // string.GetHashCode is process-randomized; this small FNV-1a seed is stable across runs and platforms.
    private static int StableTaffySeed(TaffyGhostCacheKey key)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (byte value in key.OwnerId.ToByteArray())
            {
                hash = (hash ^ value) * 16777619;
            }

            foreach (char value in key.ValueText)
            {
                hash = (hash ^ value) * 16777619;
            }

            hash = (hash ^ (key.IsLiteral ? 1u : 0u)) * 16777619;
            return (int)(hash & 0x7FFFFFFF);
        }
    }

    internal static (InkBounds Anchor, double LineHeight)? FindSpawn(IReadOnlyList<RecognizedToken> tokens)
    {
        int equalsIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Latex == "=") equalsIndex = i;
        }

        if (equalsIndex < 0) return null;
        return (tokens[equalsIndex].Bounds, ClampedMedianTokenHeight(tokens));
    }

    /// <summary>
    /// A line's representative glyph height: the clamped median height of its non-<c>=</c> tokens (the
    /// <c>=</c> is excluded because its bounds don't track the digits' size). Clamped to [24, 96] and
    /// defaulting to 48 for a line with no sizeable tokens. Shared by answer spawning and 5.3 stamp
    /// rescaling so a stamped answer matches the line it lands on the same way a fresh answer is sized.
    /// </summary>
    internal static double ClampedMedianTokenHeight(IReadOnlyList<RecognizedToken> tokens)
    {
        double[] heights = tokens.Where(token => token.Latex != "=").Select(token => token.Bounds.Height).Order().ToArray();
        double median = heights.Length == 0
            ? 48
            : heights.Length % 2 == 1 ? heights[heights.Length / 2] : (heights[heights.Length / 2 - 1] + heights[heights.Length / 2]) / 2;
        return Math.Clamp(median, 24, 96);
    }

    /// <summary>Builds schema v3 from raw ink plus neutral cache snapshots; no graph edges are serialized.</summary>
    public PenumbraDocument CreateDocumentSnapshot()
    {
        PersistedRegion[] regions = _previousRegions.Select(region =>
        {
            SheetNode? node = _sheet.Find(region.Region.Id);
            PersistedNodeResult? result = node?.Result is null ? null : new PersistedNodeResult(
                node.Result.Latex,
                node.Result.DisplayText,
                node.Result.IsComputed,
                node.Result.Kind.ToString());
            return new PersistedRegion(
                region.Region.Id,
                region.Region.StrokeIds.ToArray(),
                region.Region.Bounds,
                new PersistedRecognition(
                    region.Result.Latex,
                    region.Result.Tokens.ToArray(),
                    region.Result.Confidence,
                    region.Result.MinConfidence),
                result);
        }).ToArray();
        return Document.ToDocument() with { Version = PenumbraDocumentSerializer.SchemaVersion, Regions = regions };
    }

    /// <summary>
    /// Loads raw ink first, then treats valid v3 snapshots only as recognition cache input. Sheet edges,
    /// roles, conflicts and results are always rebuilt through segmentation + Upsert + recompute; persisted
    /// results are never authoritative. v1/v2 naturally supply an empty cache and take the same path.
    /// </summary>
    public async Task LoadDocumentAsync(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        EndTaffyCore(resignalRecognition: false);
        _liveDebouncer.Cancel();
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

        _bankedStrokeSets.Clear();
        _previousRegions = BuildValidLoadCache(document);
        _lastAppliedRegions = Array.Empty<RegionRecognition>();
        AnswerLayer = AnswerLayer.Empty;
        LiteralRunLayer = LiteralRunLayer.Empty;
        CausalityRipple = null;
        ProvenanceStrokeIds = NoStrokes;
        await RecognizeCoreAsync(RecognitionMode.Load);
    }

    private static IReadOnlyList<RegionRecognition> BuildValidLoadCache(PenumbraDocument document)
    {
        IReadOnlyList<PersistedRegion> persistedRegions = document.Regions ?? Array.Empty<PersistedRegion>();
        if (document.Version < 3 || persistedRegions.Count == 0)
        {
            return Array.Empty<RegionRecognition>();
        }

        // Duplicate stroke ids make a reference ambiguous, so invalidate all cached regions rather than
        // guessing. Raw ink still loads and will be freshly recognized.
        HashSet<Guid> ids = document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        if (ids.Count != document.Strokes.Count)
        {
            return Array.Empty<RegionRecognition>();
        }

        Guid[] persistedRegionIds = persistedRegions.Select(region => region.Id).ToArray();
        Guid[] referencedStrokeIds = persistedRegions.SelectMany(region => region.StrokeIds).ToArray();
        if (persistedRegionIds.Any(id => id == Guid.Empty)
            || persistedRegionIds.Distinct().Count() != persistedRegionIds.Length
            || referencedStrokeIds.Distinct().Count() != referencedStrokeIds.Length)
        {
            // Regions form a partition. Duplicate region ids or cross-region stroke ownership make stable
            // matching ambiguous; discard the cache wholesale and let current segmentation reconstruct it.
            return Array.Empty<RegionRecognition>();
        }

        var valid = new List<RegionRecognition>();
        foreach (PersistedRegion region in persistedRegions)
        {
            HashSet<Guid> regionIds = region.StrokeIds.ToHashSet();
            bool validRegion = regionIds.Count == region.StrokeIds.Count
                && regionIds.Count > 0
                && regionIds.All(ids.Contains)
                && region.Recognition.Tokens.All(token =>
                    token.SourceStrokeIds.Count > 0 && token.SourceStrokeIds.All(regionIds.Contains));
            if (!validRegion)
            {
                continue;
            }

            var placeholder = new InkRegion(region.Id, region.StrokeIds.ToArray(), region.Bounds, Array.Empty<StrokeGroup>());
            var result = new RecognitionResult(
                region.Recognition.Latex,
                region.Recognition.Tokens.ToArray(),
                region.Recognition.Confidence,
                region.Recognition.MinConfidence);
            valid.Add(new RegionRecognition(placeholder, result, Dirty: false));
        }

        return valid;
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
        if (!value) _liveDebouncer.Cancel();
        else if (Document.Strokes.Count > 0) _liveDebouncer.Signal();
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
                _liveDebouncer.Signal(QuietPeriodFor(previousStrokeCount, Document.Strokes.Count));
            }
        }
    }

    /// <summary>
    /// The debounce window for a document change: stroke-removing edits (erase, undo of an add) wait
    /// the longer erase grace because a rewrite usually follows; everything else uses the live period.
    /// </summary>
    internal static TimeSpan QuietPeriodFor(int previousStrokeCount, int currentStrokeCount) =>
        currentStrokeCount < previousStrokeCount ? EraseQuietPeriod : LiveQuietPeriod;

    private void ResetTransientState()
    {
        _liveDebouncer.Cancel();
        CancelRecognition();
        ++_recognitionGeneration;
        foreach (Guid id in _sheet.Nodes.Select(node => node.Id).ToArray()) _sheet.Remove(id);
        _sheet.RecomputeDetailed();
        _previousRegions = Array.Empty<RegionRecognition>();
        _lastAppliedRegions = Array.Empty<RegionRecognition>();
        AnswerLayer = AnswerLayer.Empty;
        LiteralRunLayer = LiteralRunLayer.Empty;
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
        _liveDebouncer.Dispose();
        EndTaffyCore(resignalRecognition: false);
        CancelRecognition();
    }

    private enum RecognitionMode { Live, Manual, Load }

    private sealed class TaffySession(
        Guid ownerId,
        LiteralRun run,
        IReadOnlyList<RecognizedToken> tokens,
        string originalValueText,
        string lastValueText,
        DateTimeOffset lastProbeAt)
    {
        public Guid OwnerId { get; } = ownerId;
        public LiteralRun Run { get; } = run;
        public IReadOnlyList<RecognizedToken> Tokens { get; } = tokens;
        public string OriginalValueText { get; } = originalValueText;
        public string LastValueText { get; set; } = lastValueText;
        public DateTimeOffset LastProbeAt { get; set; } = lastProbeAt;
    }

    private readonly record struct TaffyGhostCacheKey(Guid OwnerId, string ValueText, bool IsLiteral);
    private sealed record LiteralDropTarget(Guid OwnerId, LiteralRun Run);

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
