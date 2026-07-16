using CommunityToolkit.Mvvm.ComponentModel;
using Penumbra.Graphing;
using Penumbra.Recognition;

namespace Penumbra.App.ViewModels;

/// <summary>
/// One curve ready to be drawn: a stable identity (<see cref="OwnerId"/>, the owning line's region id), a
/// display <see cref="Name"/> (<c>"y = x^2"</c>), the variable names for axis labelling, the gap-segmented
/// <see cref="Series"/>, and a <see cref="ColorIndex"/> that stays fixed for as long as the owning line keeps
/// producing a candidate — re-publishing the same owner never reassigns its color, even if other curves come
/// and go around it.
/// </summary>
public sealed record GraphCurveModel(
    Guid OwnerId,
    string Name,
    string DependentVariable,
    string IndependentVariable,
    GraphSeries Series,
    int ColorIndex);

/// <summary>
/// Headless graphing logic for Phase 6: watches the page's accepted-region state, runs <see cref="IGraphDetector"/>
/// over every accepted line (tree preferred, LaTeX string fallback), samples every accepted candidate through
/// <see cref="IDomainSampler"/>, and exposes the result as plottable <see cref="GraphCurveModel"/>s. Carries no
/// Avalonia, AngouriMath, or ScottPlot dependency — only the <c>Penumbra.Graphing</c> contracts — so it is
/// fully unit-testable without a UI.
/// </summary>
public sealed partial class GraphPanelViewModel : ViewModelBase
{
    /// <summary>The default sampling domain's lower bound, used until the user pans/zooms the panel.</summary>
    public const double DefaultDomainMin = -10;

    /// <summary>The default sampling domain's upper bound, used until the user pans/zooms the panel.</summary>
    public const double DefaultDomainMax = 10;

    /// <summary>
    /// The default sample count per curve. 400 points across the default [-10, 10] span is one sample every
    /// 0.05 units — dense enough to read smooth on a normal panel width without approaching
    /// <see cref="Graphing.DomainSampler.MaxSampleCount"/>.
    /// </summary>
    public const int DefaultSampleCount = 400;

    private readonly IGraphDetector _detector;
    private readonly IDomainSampler _sampler;

    // Every currently graphable line, keyed by its owning region id, so a domain change (pan/zoom) can
    // re-sample every candidate without re-running detection.
    private readonly Dictionary<Guid, GraphCandidate> _candidatesByOwner = new();

    // Assigned once per owner and never reassigned while that owner keeps appearing across recognition
    // passes — this is the "stable per-curve color index" the panel promises. Only Clear() resets it, because
    // that is the one operation that means "this is a different page now."
    private readonly Dictionary<Guid, int> _colorIndexByOwner = new();
    private int _nextColorIndex;

    public GraphPanelViewModel(IGraphDetector detector, IDomainSampler sampler)
    {
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(sampler);
        _detector = detector;
        _sampler = sampler;
        Domain = GraphDomain.Create(DefaultDomainMin, DefaultDomainMax);
    }

    /// <summary>The domain every current curve was last sampled over.</summary>
    public GraphDomain Domain { get; private set; }

    /// <summary>Every currently plottable curve, ordered by <see cref="GraphCurveModel.ColorIndex"/>.</summary>
    [ObservableProperty]
    private IReadOnlyList<GraphCurveModel> _curves = Array.Empty<GraphCurveModel>();

    /// <summary>True once at least one curve exists — the View's hook to auto-show/auto-hide the panel.</summary>
    public bool HasCurves => Curves.Count > 0;

    partial void OnCurvesChanged(IReadOnlyList<GraphCurveModel> value) => OnPropertyChanged(nameof(HasCurves));

    /// <summary>
    /// Re-runs detection over the page's currently accepted lines and re-samples every resulting candidate
    /// over the current <see cref="Domain"/>. Called by the host once per applied recognition/Sheet
    /// transaction (see <c>MainWindowViewModel.ApplyRegions</c>) — a line whose region no longer appears
    /// simply drops out; a non-graphable line was never a candidate and never appears. Never throws for
    /// ordinary non-graph math: a detection rejection or a sampling refusal both just omit that line's curve.
    /// </summary>
    public void UpdateFromAcceptedRegions(IReadOnlyList<RegionRecognition> acceptedRegions)
    {
        ArgumentNullException.ThrowIfNull(acceptedRegions);

        _candidatesByOwner.Clear();
        foreach (RegionRecognition region in acceptedRegions)
        {
            GraphDetectionOutcome outcome = DetectRegion(region);
            if (outcome.IsAccepted)
            {
                _candidatesByOwner[region.Region.Id] = outcome.Candidate!;
            }
        }

        Resample();
    }

    /// <summary>
    /// Applies a new sampling domain (a pan/zoom) and re-samples every tracked candidate over it through the
    /// same <see cref="IDomainSampler"/> — detection does not re-run, only sampling.
    /// </summary>
    public void SetDomain(double min, double max)
    {
        Domain = GraphDomain.Create(min, max);
        Resample();
    }

    /// <summary>
    /// Drops every tracked candidate and color assignment — the page-cleared case. A fresh page starts color
    /// assignment over from the beginning.
    /// </summary>
    public void Clear()
    {
        _candidatesByOwner.Clear();
        _colorIndexByOwner.Clear();
        _nextColorIndex = 0;
        Curves = Array.Empty<GraphCurveModel>();
    }

    /// <summary>
    /// Tree preferred, LaTeX string fallback — both <see cref="IGraphDetector"/> entry points read the same
    /// grammar. Every region reaching this method already passed <c>RecognitionGate</c>, so when
    /// <see cref="RecognitionResult.ParseOutcome"/> is non-null it is necessarily
    /// <see cref="Penumbra.Core.Layout.ParseOutcomeKind.Accepted"/> (a refused/ambiguous outcome fails the gate and the
    /// region never reaches "accepted" at all) — a null outcome means the caller carries no structural
    /// opinion (test fakes, <c>NoOpRecognizer</c>), and <see cref="RecognitionResult.Latex"/> is the same safe
    /// string in both cases.
    /// </summary>
    private GraphDetectionOutcome DetectRegion(RegionRecognition region) =>
        region.Result.ParseOutcome is { Root: { } root }
            ? _detector.Detect(root)
            : _detector.Detect(region.Result.Latex);

    private void Resample()
    {
        var models = new List<GraphCurveModel>(_candidatesByOwner.Count);
        foreach (KeyValuePair<Guid, GraphCandidate> pair in _candidatesByOwner)
        {
            GraphSamplingOutcome sampled = _sampler.SampleSeries(pair.Value, Domain, DefaultSampleCount);
            if (!sampled.IsSampled)
            {
                continue;
            }

            models.Add(new GraphCurveModel(
                pair.Key,
                BuildName(pair.Value),
                pair.Value.DependentVariable,
                pair.Value.IndependentVariable,
                sampled.Series!,
                ColorIndexFor(pair.Key)));
        }

        models.Sort((a, b) => a.ColorIndex.CompareTo(b.ColorIndex));
        Curves = models;
    }

    private int ColorIndexFor(Guid ownerId)
    {
        if (_colorIndexByOwner.TryGetValue(ownerId, out int index))
        {
            return index;
        }

        index = _nextColorIndex++;
        _colorIndexByOwner[ownerId] = index;
        return index;
    }

    private static string BuildName(GraphCandidate candidate) =>
        $"{candidate.DependentVariable} = {candidate.ExpressionLatex}";
}
