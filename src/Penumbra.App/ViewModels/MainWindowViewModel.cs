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
        IGlyphBank? glyphBank = null)
        : this()
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(graphDetector);
        _recognizer = recognizer;
        _evaluator = evaluator;
        _glyphBank = glyphBank;
    }

    /// <summary>The page being drawn on; owns the strokes and the undo/redo history.</summary>
    public InkDocument Document { get; }

    /// <summary>What the recognizer last read / computed (shown under the canvas).</summary>
    [ObservableProperty]
    private string _recognitionText = "Write an expression ending in '=', then press Recognize";

    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private void Recognize()
    {
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
            RecognitionText = answer.IsComputed
                ? $"{expression}  {answer.DisplayText}"
                : $"read:  {expression}      (couldn't compute: {answer.DisplayText})";
            return;
        }

        RecognitionText = $"read:  {expression}      ({result.Confidence:P0} confident)";
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

    private void OnDocumentChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        RecognizeCommand.NotifyCanExecuteChanged();
    }
}
