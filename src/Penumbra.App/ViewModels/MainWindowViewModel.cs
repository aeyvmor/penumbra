using Penumbra.Cas;
using Penumbra.Graphing;
using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        Greeting = "Penumbra";
        Status = "Workspace ready";
    }

    public MainWindowViewModel(
        IEvaluator evaluator,
        IRecognizer recognizer,
        IGraphDetector graphDetector,
        IStrokeSmoother strokeSmoother)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(graphDetector);
        ArgumentNullException.ThrowIfNull(strokeSmoother);

        Greeting = "Penumbra";
        Status = "Phase 0 shell loaded";
    }

    public string Greeting { get; }

    public string Status { get; }
}
