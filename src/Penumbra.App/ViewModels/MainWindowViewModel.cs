using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Graphing;
using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>();
    private static readonly IReadOnlySet<Guid> NoStrokes = new HashSet<Guid>();
    private const string IdleHint = "Write an expression ending in '=' — it answers when you pause";

    /// <summary>
    /// The recognizer's decision contract (audit B4): the reject bar (<c>MinConfidence</c> — reads whose
    /// weakest symbol scores below it are refused) and the stricter banking bar (<c>BankConfidence</c>);
    /// temperature scaling and energy rejection are applied inside the classifier itself. Injected from
    /// the model's meta.json via DI so the bars always match the confidences that model actually
    /// produces; <see cref="RecognitionCalibration.Default"/> (reject 0.55, bank 0.80 — the pre-B4
    /// hardcoded constants) for design time and models whose meta predates calibration.
    /// </summary>
    private readonly RecognitionCalibration _calibration = RecognitionCalibration.Default;

    /// <summary>
    /// 4.5b: how long the pen must stay up before a live read fires. Tuned by dogfood: 600 ms fired
    /// between the two strokes of a '+' (mouse repositioning gaps run 600–900 ms), glitching the
    /// half-drawn symbol mid-writing. If the pen retest still trips this, the designed escalation is
    /// a two-tier debounce (read fast, show glitch/reject UX only after a longer quiet), not more delay.
    /// </summary>
    internal static readonly TimeSpan LiveQuietPeriod = TimeSpan.FromSeconds(1);

    private readonly IRecognizer _recognizer;
    private readonly IEvaluator? _evaluator;
    private readonly IGlyphBank? _glyphBank;
    private readonly HandwritingSynthesizer? _synthesizer;
    private readonly Debouncer _liveDebouncer;

    // 4.5b corpus-poison guard: the stroke-sets already banked this page, so re-reads never re-bank
    // the same physical ink (see GlyphCapture dedup overload). Cleared when the page empties.
    private readonly HashSet<string> _bankedStrokeSets = new();

    // The in-flight read's cancellation, superseded by every newer read and by pen-down.
    private CancellationTokenSource? _recognitionCts;

    // Bumped on every animation so the freshly-built AnswerAnimation never compares equal to the last one,
    // guaranteeing the canvas's styled property fires even for a re-computed identical answer.
    private long _animationSequence;

    // What the current AnswerAnimation shows, as (recognized latex, answer text) — live re-reads of the
    // same line skip re-playing an identical answer (an unrelated edit elsewhere must not replay it).
    private (string Latex, string Answer)? _lastAnimated;

    // Seam-1 tokens behind the displayed answer — the ghost-trace provenance source (4.5d).
    private IReadOnlyList<RecognizedToken> _lastAnsweredTokens = Array.Empty<RecognizedToken>();

    public MainWindowViewModel()
    {
        Document = new InkDocument();
        Document.Changed += (_, _) => OnDocumentChanged();
        _recognizer = new NoOpRecognizer();   // design-time fallback; DI overrides below

        // The debouncer fires on a timer thread; recognition + all state changes belong to the UI thread.
        _liveDebouncer = new Debouncer(
            LiveQuietPeriod,
            () => Dispatcher.UIThread.Post(() => _ = RecognizeCoreAsync(manual: false)));
    }

    public MainWindowViewModel(
        IEvaluator evaluator,
        IRecognizer recognizer,
        IGraphDetector graphDetector,
        IGlyphBank? glyphBank = null,
        HandwritingSynthesizer? synthesizer = null,
        RecognitionCalibration? calibration = null)
        : this()
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(graphDetector);
        _recognizer = recognizer;
        _evaluator = evaluator;
        _glyphBank = glyphBank;
        _synthesizer = synthesizer;
        _calibration = calibration ?? RecognitionCalibration.Default;
    }

    /// <summary>The page being drawn on; owns the strokes and the undo/redo history.</summary>
    public InkDocument Document { get; }

    /// <summary>What the recognizer last read / computed (shown under the canvas).</summary>
    [ObservableProperty]
    private string _recognitionText = IdleHint;

    /// <summary>
    /// The answer layer the canvas plays on top of the ink, or null when there's nothing to animate.
    /// Deliberately separate from <see cref="Document"/> so synthesized ink never re-enters recognition.
    /// </summary>
    [ObservableProperty]
    private AnswerAnimation? _answerAnimation;

    /// <summary>4.5b: recognize automatically a beat after pen-lift. The button remains as a manual re-read.</summary>
    [ObservableProperty]
    private bool _liveRecognition = true;

    /// <summary>4.5c: strokes of below-threshold symbols; the canvas renders them as glitch-ink.</summary>
    [ObservableProperty]
    private IReadOnlySet<Guid> _uncertainStrokeIds = NoStrokes;

    /// <summary>4.5d: strokes to highlight as the displayed answer's provenance (empty = no highlight).</summary>
    [ObservableProperty]
    private IReadOnlySet<Guid> _provenanceStrokeIds = NoStrokes;

    /// <summary>
    /// 4.5b: the pen touched down. A pending or in-flight read is stale by construction — its snapshot
    /// misses the stroke being drawn — and an answer materializing mid-stroke is jank, so both die here.
    /// </summary>
    public void NotifyStrokeStarted()
    {
        _liveDebouncer.Cancel();
        _recognitionCts?.Cancel();
    }

    /// <summary>
    /// 4.5d: tap on the played answer — toggle the highlight of the ink it was recognized from.
    /// Seam 1 makes this a set-union: every answered token knows its source strokes.
    /// </summary>
    public void ToggleAnswerProvenance()
    {
        if (ProvenanceStrokeIds.Count > 0)
        {
            ProvenanceStrokeIds = NoStrokes;
            return;
        }

        var ids = new HashSet<Guid>();
        foreach (RecognizedToken token in _lastAnsweredTokens)
        {
            foreach (Guid id in token.SourceStrokeIds)
            {
                ids.Add(id);
            }
        }

        ProvenanceStrokeIds = ids;
    }

    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private Task Recognize() => RecognizeCoreAsync(manual: true);

    /// <summary>
    /// The one recognition path (button and live debounce both land here). Snapshots the strokes —
    /// <see cref="InkDocument.Strokes"/> is the live list and this leaves the UI thread — starts a
    /// fresh cancellation scope that supersedes any older read, and applies the result unless a newer
    /// read (or pen-down) cancelled it first.
    /// </summary>
    private async Task RecognizeCoreAsync(bool manual)
    {
        if (Document.Strokes.Count == 0)
        {
            return;
        }

        if (manual)
        {
            // Manual semantics predate live mode: a clicked re-read supersedes the playing answer up
            // front, so every failure path below leaves a clean canvas. Live reads are gentler — they
            // keep the last answer through transient mid-writing failures.
            AnswerAnimation = null;
            _lastAnimated = null;
        }

        _recognitionCts?.Cancel();
        _recognitionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _recognitionCts = cts;

        Stroke[] snapshot = Document.Strokes.ToArray();

        RecognitionResult result;
        try
        {
            result = await _recognizer.RecognizeAsync(snapshot, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;   // superseded — the newer read owns the UI
        }

        if (cts.Token.IsCancellationRequested)
        {
            return;
        }

        ApplyRecognition(result, snapshot, manual);
    }

    private void ApplyRecognition(RecognitionResult result, IReadOnlyList<Stroke> snapshot, bool manual)
    {
        // Any fresh read invalidates a provenance highlight — it described the previous answer.
        ProvenanceStrokeIds = NoStrokes;

        if (string.IsNullOrEmpty(result.Latex))
        {
            RecognitionText = "(couldn't read that — try clearer symbols)";
            UncertainStrokeIds = NoStrokes;
            return;
        }

        // 3.9c: refuse to compute on a shaky read, naming the ambiguous symbol rather than guessing.
        // 4.5c: and give the refusal a body — the offending strokes themselves glitch on the canvas.
        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, _calibration.MinConfidence);
        if (!gate.Accepted)
        {
            RecognitionText = gate.Refusal!;
            UncertainStrokeIds = RecognitionGate.UncertainStrokeIds(result, _calibration.MinConfidence);
            return;
        }

        UncertainStrokeIds = NoStrokes;
        string expression = result.Latex;

        // A trailing '=' is the "compute me" trigger; otherwise just echo what was read.
        if (_evaluator is not null && expression.EndsWith('='))
        {
            EvaluationResult answer = _evaluator.Evaluate(new EvaluationRequest(expression, NoVariables));

            // The typeset text is the honest, always-present readout; the animation SUPPLEMENTS it.
            RecognitionText = answer.IsComputed
                ? $"{expression}  {answer.DisplayText}"
                : $"read:  {expression}      (couldn't compute: {answer.DisplayText})";

            if (answer.IsComputed)
            {
                // 4.5b: bank only here — a computed '='-read means the expression was FINISHED, so no
                // partial glyph (the first bar of a live-written '=' reads as a confident '-') can ever
                // enter the corpus. The dedup set stops re-reads from banking the same ink twice.
                if (_glyphBank is not null)
                {
                    foreach (GlyphSample sample in GlyphCapture.Collect(
                        result.Tokens, snapshot, _calibration.BankConfidence, DateTimeOffset.UtcNow, _bankedStrokeSets))
                    {
                        _glyphBank.Capture(sample);
                    }
                }

                AnimateAnswer(answer.DisplayText, result, manual);
            }

            return;
        }

        RecognitionText = $"read:  {expression}      ({result.Confidence:P0} confident)";
    }

    /// <summary>
    /// Plays the answer unless this is a live re-read of exactly what's already showing — the page
    /// re-reads on every pen-lift, and replaying an unchanged answer each time is noise. The manual
    /// button always replays (the user asked).
    /// </summary>
    private void AnimateAnswer(string answerText, RecognitionResult result, bool manual)
    {
        (string Latex, string Answer) key = (result.Latex, answerText);
        if (!manual && AnswerAnimation is not null && _lastAnimated == key)
        {
            return;
        }

        if (TryBuildAnimation(answerText, result.Tokens) is { } animation)
        {
            AnswerAnimation = animation;
            _lastAnimated = key;
            _lastAnsweredTokens = result.Tokens;
        }
    }

    /// <summary>
    /// Best-effort: build the animated answer spawning from the '=' sign. Silently yields null (leaving the
    /// typeset text as the sole readout) when there's no synthesizer, no '=' anchor, or any output symbol the
    /// sources can't supply — an honest fallback until the 4e font source guarantees full coverage.
    /// </summary>
    private AnswerAnimation? TryBuildAnimation(string answerText, IReadOnlyList<RecognizedToken> tokens)
    {
        (InkBounds Anchor, double LineHeight)? spawn = FindSpawn(tokens);
        if (_synthesizer is null || spawn is null)
        {
            return null;
        }

        (InkBounds anchor, double lineHeight) = spawn.Value;
        var options = new SynthesisOptions { LineHeight = lineHeight };

        // Draw the handwriting form, not the raw CAS surface: "4 * y" must be written "4y" (juxtaposition),
        // never traced literally with a scribbly '*'. The typeset RecognitionText keeps the raw DisplayText.
        string handwriting = HandwritingText.FromDisplayText(answerText);

        // Unseeded in the app so repeated identical answers get fresh jitter; tests inject a seeded Random.
        SynthesizedHandwriting? synthesized = _synthesizer.Synthesize(handwriting, anchor, options, new Random());
        if (synthesized is null || synthesized.MissingSymbols.Count > 0)
        {
            return null;
        }

        return new AnswerAnimation(synthesized, ++_animationSequence);
    }

    /// <summary>
    /// Picks the animation spawn: the LAST '=' token's bounds is the anchor, and the line height is the median
    /// height of the non-'=' tokens (the '=' itself is short, so it's excluded), clamped to a sane pen range.
    /// Pure and static so it's testable headlessly. Null when there's no '=' to spawn from.
    /// </summary>
    internal static (InkBounds Anchor, double LineHeight)? FindSpawn(IReadOnlyList<RecognizedToken> tokens)
    {
        int equalsIndex = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Latex == "=")
            {
                equalsIndex = i;
            }
        }

        if (equalsIndex < 0)
        {
            return null;
        }

        var heights = new List<double>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Latex != "=")
            {
                heights.Add(tokens[i].Bounds.Height);
            }
        }

        double lineHeight = Math.Clamp(Median(heights, fallback: 48.0), 24.0, 96.0);
        return (tokens[equalsIndex].Bounds, lineHeight);
    }

    /// <summary>Median of the values, or <paramref name="fallback"/> when the list is empty.</summary>
    private static double Median(List<double> values, double fallback)
    {
        if (values.Count == 0)
        {
            return fallback;
        }

        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
    }

    private bool CanRecognize => Document.Strokes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Document.Undo();

    private bool CanUndo => Document.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Document.Redo();

    private bool CanRedo => Document.CanRedo;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear() =>
        // Emptying the page triggers the full transient reset (answer, glitch, provenance, banked keys)
        // via OnDocumentChanged — one code path whether the page empties by Clear, undo, or load.
        Document.Clear();

    private bool CanClear => Document.Strokes.Count > 0;

    partial void OnLiveRecognitionChanged(bool value)
    {
        if (value)
        {
            // Turning live on with ink already on the page: read it after one quiet period.
            if (Document.Strokes.Count > 0)
            {
                _liveDebouncer.Signal();
            }
        }
        else
        {
            _liveDebouncer.Cancel();
        }
    }

    private void OnDocumentChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RecognizeCommand.NotifyCanExecuteChanged();

        // Whatever the highlight pointed at, the page just changed under it.
        ProvenanceStrokeIds = NoStrokes;

        if (Document.Strokes.Count == 0)
        {
            ResetTransientState();
            return;
        }

        // 4.5b: the pen lifted (or undo/redo/load changed the ink) — read the page after a quiet beat.
        if (LiveRecognition)
        {
            _liveDebouncer.Signal();
        }
    }

    /// <summary>The page is empty: nothing to read, answer, glitch, highlight, or remember.</summary>
    private void ResetTransientState()
    {
        _liveDebouncer.Cancel();
        _recognitionCts?.Cancel();
        AnswerAnimation = null;
        _lastAnimated = null;
        _lastAnsweredTokens = Array.Empty<RecognizedToken>();
        UncertainStrokeIds = NoStrokes;
        ProvenanceStrokeIds = NoStrokes;
        _bankedStrokeSets.Clear();
        RecognitionText = IdleHint;
    }

    public void Dispose()
    {
        _liveDebouncer.Dispose();
        _recognitionCts?.Cancel();
        _recognitionCts?.Dispose();
        _recognitionCts = null;
    }
}
