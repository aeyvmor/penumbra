using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Penumbra.Cas;
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
    private readonly HashSet<string> _bankedStrokeSets = new();

    // This list is the recognizer's complete round-trip state, including rejected regions. It is replaced
    // only by an atomically applied latest pass; a cancelled/superseded result never becomes cache authority.
    private IReadOnlyList<RegionRecognition> _previousRegions = Array.Empty<RegionRecognition>();
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
        RecognitionCalibration? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(regionRecognizer);
        ArgumentNullException.ThrowIfNull(sheet);

        _regionRecognizer = regionRecognizer;
        _sheet = sheet;
        _glyphBank = glyphBank;
        _synthesizer = synthesizer;
        _calibration = calibration ?? RecognitionCalibration.Default;

        Document = new InkDocument();
        Document.Changed += (_, _) => OnDocumentChanged();
        _liveDebouncer = new Debouncer(
            LiveQuietPeriod,
            () => Dispatcher.UIThread.Post(() => _ = RecognizeCoreAsync(RecognitionMode.Live)));
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

        foreach (RegionRecognition region in regions.Where(r => accepted.ContainsKey(r.Region.Id)))
        {
            _sheet.Upsert(region.Region.Id, region.Result.Latex, region.Result.Tokens, region.Region.Bounds);
        }

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
        node.Role == NodeRole.Query && !node.IsConflict && node.Result is { IsComputed: true };

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

    internal static (InkBounds Anchor, double LineHeight)? FindSpawn(IReadOnlyList<RecognizedToken> tokens)
    {
        int equalsIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Latex == "=") equalsIndex = i;
        }

        if (equalsIndex < 0) return null;
        double[] heights = tokens.Where(token => token.Latex != "=").Select(token => token.Bounds.Height).Order().ToArray();
        double median = heights.Length == 0
            ? 48
            : heights.Length % 2 == 1 ? heights[heights.Length / 2] : (heights[heights.Length / 2 - 1] + heights[heights.Length / 2]) / 2;
        return (tokens[equalsIndex].Bounds, Math.Clamp(median, 24, 96));
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
        AnswerLayer = AnswerLayer.Empty;
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
        else if (LiveRecognition)
        {
            _liveDebouncer.Signal(QuietPeriodFor(previousStrokeCount, Document.Strokes.Count));
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
        AnswerLayer = AnswerLayer.Empty;
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
        CancelRecognition();
    }

    private enum RecognitionMode { Live, Manual, Load }

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
