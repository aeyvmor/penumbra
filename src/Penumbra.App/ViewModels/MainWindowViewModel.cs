using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Graphing;
using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly IReadOnlyDictionary<string, string> NoVariables = new Dictionary<string, string>();

    /// <summary>
    /// Reject reads whose weakest symbol scores below this. Initial guess — the Phase 3.9 plan tunes it
    /// empirically on real ink, and a principled per-class calibration ships with the next recognizer
    /// retrain. The same bar decides which glyphs are confident enough to bank.
    /// </summary>
    public const double RejectThreshold = 0.55;

    private readonly IRecognizer _recognizer;
    private readonly IEvaluator? _evaluator;
    private readonly IGlyphBank? _glyphBank;
    private readonly HandwritingSynthesizer? _synthesizer;

    // Bumped on every animation so the freshly-built AnswerAnimation never compares equal to the last one,
    // guaranteeing the canvas's styled property fires even for a re-computed identical answer.
    private long _animationSequence;

    public MainWindowViewModel()
    {
        Document = new InkDocument();
        Document.Changed += (_, _) => OnDocumentChanged();
        _recognizer = new NoOpRecognizer();   // design-time fallback; DI overrides below
    }

    public MainWindowViewModel(
        IEvaluator evaluator,
        IRecognizer recognizer,
        IGraphDetector graphDetector,
        IGlyphBank? glyphBank = null,
        HandwritingSynthesizer? synthesizer = null)
        : this()
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(graphDetector);
        _recognizer = recognizer;
        _evaluator = evaluator;
        _glyphBank = glyphBank;
        _synthesizer = synthesizer;
    }

    /// <summary>The page being drawn on; owns the strokes and the undo/redo history.</summary>
    public InkDocument Document { get; }

    /// <summary>What the recognizer last read / computed (shown under the canvas).</summary>
    [ObservableProperty]
    private string _recognitionText = "Write an expression ending in '=', then press Recognize";

    /// <summary>
    /// The answer layer the canvas plays on top of the ink, or null when there's nothing to animate.
    /// Deliberately separate from <see cref="Document"/> so synthesized ink never re-enters recognition.
    /// </summary>
    [ObservableProperty]
    private AnswerAnimation? _answerAnimation;

    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private void Recognize()
    {
        // A new read supersedes any previously-playing answer; clear first so every failure path below
        // leaves no stale animation on the canvas.
        AnswerAnimation = null;

        RecognitionResult result = _recognizer.Recognize(Document.Strokes);
        if (string.IsNullOrEmpty(result.Latex))
        {
            RecognitionText = "(couldn't read that — try clearer symbols)";
            return;
        }

        // 3.9c: refuse to compute on a shaky read, naming the ambiguous symbol rather than guessing.
        RecognitionGate.GateResult gate = RecognitionGate.Evaluate(result, RejectThreshold);
        if (!gate.Accepted)
        {
            RecognitionText = gate.Refusal!;
            return;
        }

        // 3.9d: the read is trustworthy, so passively bank the confidently-recognized glyphs (in the
        // user's own hand) for the owned corpus — no sampling/synthesis, that's Phase 4b.
        if (_glyphBank is not null)
        {
            foreach (GlyphSample sample in GlyphCapture.Collect(
                result.Tokens, Document.Strokes, RejectThreshold, DateTimeOffset.UtcNow))
            {
                _glyphBank.Capture(sample);
            }
        }

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
                TryAnimateAnswer(answer.DisplayText, result.Tokens);
            }

            return;
        }

        RecognitionText = $"read:  {expression}      ({result.Confidence:P0} confident)";
    }

    /// <summary>
    /// Best-effort: spawn the animated answer from the '=' sign. Silently no-ops (leaving the typeset text as
    /// the sole readout) when there's no synthesizer, no '=' anchor, or any output symbol the sources can't
    /// supply — an honest fallback until the 4e font source guarantees full coverage.
    /// </summary>
    private void TryAnimateAnswer(string answerText, IReadOnlyList<RecognizedToken> tokens)
    {
        (InkBounds Anchor, double LineHeight)? spawn = FindSpawn(tokens);
        if (_synthesizer is null || spawn is null)
        {
            return;
        }

        (InkBounds anchor, double lineHeight) = spawn.Value;
        var options = new SynthesisOptions { LineHeight = lineHeight };

        // Unseeded in the app so repeated identical answers get fresh jitter; tests inject a seeded Random.
        SynthesizedHandwriting? synthesized = _synthesizer.Synthesize(answerText, anchor, options, new Random());
        if (synthesized is null || synthesized.MissingSymbols.Count > 0)
        {
            return;
        }

        AnswerAnimation = new AnswerAnimation(synthesized, ++_animationSequence);
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
    private void Clear()
    {
        // Clearing the page also wipes the answer layer. Adding a stroke does NOT (see OnDocumentChanged):
        // the user may keep writing beside a played answer, and it stays until Clear or the next Recognize.
        Document.Clear();
        AnswerAnimation = null;
    }

    private bool CanClear => Document.Strokes.Count > 0;

    private void OnDocumentChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RecognizeCommand.NotifyCanExecuteChanged();
    }
}
