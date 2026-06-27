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

    private readonly IRecognizer _recognizer;
    private readonly IEvaluator? _evaluator;

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
        IStrokeSmoother strokeSmoother)
        : this()
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(graphDetector);
        ArgumentNullException.ThrowIfNull(strokeSmoother);
        _recognizer = recognizer;
        _evaluator = evaluator;
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
