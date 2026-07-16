using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Penumbra.App.ViewModels;
using ScottPlot;
using ScottPlot.Plottables;

namespace Penumbra.App.Views;

/// <summary>
/// The ScottPlot host for <see cref="GraphPanelViewModel"/>. Deliberately thin: every decision about WHAT to
/// plot (detection, sampling, naming, segmentation, color identity) lives in the headless ViewModel; this
/// code-behind only projects <see cref="GraphCurveModel"/>s into ScottPlot plottables, forwards the built-in
/// pan/zoom interaction back as a debounced <see cref="GraphPanelViewModel.SetDomain"/> re-sample, and drives
/// the crosshair readout. Each <c>GraphSegment</c> becomes its own scatter line so a gap (asymptote, domain
/// edge) is never bridged by a fake connecting line.
/// </summary>
public partial class GraphPanelView : UserControl
{
    /// <summary>How long the axes must hold still after a pan/zoom before re-sampling the visible range.</summary>
    private static readonly TimeSpan ResampleQuietPeriod = TimeSpan.FromMilliseconds(250);

    private readonly IPalette _palette = new ScottPlot.Palettes.Category10();
    private readonly DispatcherTimer _resampleTimer;
    private GraphPanelViewModel? _viewModel;
    private Crosshair? _crosshair;
    private bool _rebuildingPlot;
    private bool _applyingDomainFromView;

    public GraphPanelView()
    {
        InitializeComponent();

        _resampleTimer = new DispatcherTimer { Interval = ResampleQuietPeriod };
        _resampleTimer.Tick += OnResampleQuietPeriodElapsed;

        DataContextChanged += (_, _) => AttachViewModel(DataContext as GraphPanelViewModel);

        // ScottPlot's own UserInputProcessor implements pan/zoom; this render-side event is how those
        // interactions surface. Guarded so our own rebuilds/limit-setting never schedule a re-sample.
        Plot.Plot.RenderManager.AxisLimitsChanged += (_, _) =>
        {
            if (!_rebuildingPlot)
            {
                _resampleTimer.Stop();
                _resampleTimer.Start();
            }
        };

        Plot.PointerMoved += OnPlotPointerMoved;
        Plot.PointerExited += OnPlotPointerExited;
    }

    private void AttachViewModel(GraphPanelViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            RebuildPlot(resetAxesToDomain: true);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphPanelViewModel.Curves))
        {
            // A pan/zoom re-sample must keep the user's axes; any other publication (new/changed/removed
            // curves from recognition) frames the ViewModel's domain afresh.
            RebuildPlot(resetAxesToDomain: !_resampleTimer.IsEnabled && !_applyingDomainFromView);
        }
    }

    private void OnResampleQuietPeriodElapsed(object? sender, EventArgs e)
    {
        _resampleTimer.Stop();
        if (_viewModel is null || _viewModel.Curves.Count == 0)
        {
            return;
        }

        AxisLimits limits = Plot.Plot.Axes.GetLimits();
        if (!double.IsFinite(limits.Left) || !double.IsFinite(limits.Right) || limits.Left >= limits.Right)
        {
            return;
        }

        if (limits.Left == _viewModel.Domain.Min && limits.Right == _viewModel.Domain.Max)
        {
            return; // nothing actually moved horizontally — no re-sample needed
        }

        _applyingDomainFromView = true;
        try
        {
            _viewModel.SetDomain(limits.Left, limits.Right);
        }
        finally
        {
            _applyingDomainFromView = false;
        }
    }

    private void RebuildPlot(bool resetAxesToDomain)
    {
        if (_viewModel is null)
        {
            return;
        }

        _rebuildingPlot = true;
        try
        {
            ScottPlot.Plot plot = Plot.Plot;
            plot.Clear();

            foreach (GraphCurveModel curve in _viewModel.Curves)
            {
                Color color = _palette.GetColor(curve.ColorIndex % _palette.Colors.Length);
                var isFirstSegment = true;
                foreach (Penumbra.Graphing.GraphSegment segment in curve.Series.Segments)
                {
                    double[] xs = segment.Points.Select(point => point.X).ToArray();
                    double[] ys = segment.Points.Select(point => point.Y).ToArray();
                    Scatter line = plot.Add.ScatterLine(xs, ys, color);
                    line.LineWidth = 2;
                    if (isFirstSegment)
                    {
                        line.LegendText = curve.Name;
                        isFirstSegment = false;
                    }
                }
            }

            if (_viewModel.Curves.Count > 0)
            {
                GraphCurveModel first = _viewModel.Curves[0];
                plot.XLabel(first.IndependentVariable);
                plot.YLabel(_viewModel.Curves.All(c => c.DependentVariable == first.DependentVariable)
                    ? first.DependentVariable
                    : "value");
                plot.ShowLegend(Alignment.UpperRight);
            }

            _crosshair = plot.Add.Crosshair(0, 0);
            _crosshair.IsVisible = false;

            if (resetAxesToDomain)
            {
                plot.Axes.SetLimitsX(_viewModel.Domain.Min, _viewModel.Domain.Max);
                plot.Axes.AutoScaleY();
            }
        }
        finally
        {
            _rebuildingPlot = false;
        }

        Plot.Refresh();
    }

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_crosshair is null)
        {
            return;
        }

        var position = e.GetPosition(Plot);
        var pixel = new Pixel(
            (float)(position.X * Plot.DisplayScale),
            (float)(position.Y * Plot.DisplayScale));
        Coordinates coordinates = Plot.Plot.GetCoordinates(pixel);

        _crosshair.Position = coordinates;
        _crosshair.IsVisible = true;
        CrosshairReadout.Text = $"x = {coordinates.X:0.###}   y = {coordinates.Y:0.###}";
        Plot.Refresh();
    }

    private void OnPlotPointerExited(object? sender, PointerEventArgs e)
    {
        if (_crosshair is null)
        {
            return;
        }

        _crosshair.IsVisible = false;
        CrosshairReadout.Text = string.Empty;
        Plot.Refresh();
    }
}
