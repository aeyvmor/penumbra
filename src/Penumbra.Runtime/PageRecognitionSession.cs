using Penumbra.Core;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.Runtime;

/// <summary>
/// Owns one page's incremental recognition cache and the recognition-to-Sheet transaction shared by
/// interactive and headless hosts. Presentation, debounce, and document editing stay host concerns.
/// </summary>
/// <remarks>
/// A recognition cycle is deliberately two-phase. <see cref="Apply"/> mutates Sheet exactly once, while
/// <see cref="Commit"/> advances the recognizer's round-trip cache only after the host has successfully
/// published its corresponding presentation state. Callers must serialize complete cycles. Epoch and
/// sequence checks still reject candidates invalidated by a load/reset or applied after a newer pass.
/// </remarks>
public sealed class PageRecognitionSession
{
    private readonly object _gate = new();
    private readonly IRegionRecognizer _recognizer;
    private readonly SheetGraph _sheet;
    private readonly double _minimumConfidence;
    private IReadOnlyList<RegionRecognition> _previousRegions = Array.Empty<RegionRecognition>();
    private IReadOnlyList<RegionRecognition> _acceptedRegions = Array.Empty<RegionRecognition>();
    private long _epoch;
    private long _nextCandidateSequence;
    private long _lastAppliedSequence;

    public PageRecognitionSession(
        IRegionRecognizer recognizer,
        SheetGraph sheet,
        double minimumConfidence)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(sheet);
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumConfidence),
                "Recognition confidence must be finite and in (0, 1].");
        }

        _recognizer = recognizer;
        _sheet = sheet;
        _minimumConfidence = minimumConfidence;
    }

    /// <summary>The complete cache authority committed by the last successful host publication.</summary>
    public IReadOnlyList<RegionRecognition> PreviousRegions
    {
        get
        {
            lock (_gate)
            {
                return _previousRegions;
            }
        }
    }

    /// <summary>The accepted line regions from the last successfully applied Sheet transaction.</summary>
    public IReadOnlyList<RegionRecognition> AcceptedRegions
    {
        get
        {
            lock (_gate)
            {
                return _acceptedRegions;
            }
        }
    }

    /// <summary>The page-owned reactive graph. Mutation remains confined to this session.</summary>
    public IReadOnlyCollection<SheetNode> SheetNodes => _sheet.Nodes;

    /// <summary>Finds a current Sheet node without exposing graph mutation authority.</summary>
    public SheetNode? FindNode(Guid id)
    {
        lock (_gate)
        {
            return _sheet.Find(id);
        }
    }

    /// <summary>Runs the page's normal non-mutating Sheet probe under the same transaction gate.</summary>
    public SheetProbeReport Probe(Guid nodeId, string trialLatex)
    {
        lock (_gate)
        {
            return _sheet.Probe(nodeId, trialLatex);
        }
    }

    /// <summary>
    /// Recognizes stable copies of the supplied ink and current cache without mutating Sheet or advancing
    /// cache authority. The host may discard the returned candidate when a newer request wins.
    /// </summary>
    public async Task<PageRecognitionCandidate> RecognizeAsync(
        IReadOnlyList<Stroke> strokes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);

        Stroke[] strokeSnapshot = strokes.ToArray();
        RegionRecognition[] previous;
        long epoch;
        long sequence;
        lock (_gate)
        {
            previous = _previousRegions.ToArray();
            epoch = _epoch;
            sequence = ++_nextCandidateSequence;
        }

        IReadOnlyList<RegionRecognition> recognized = await _recognizer
            .RecognizeRegionsAsync(strokeSnapshot, previous, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(recognized);
        if (recognized.Any(region => region.RequiresAuthoritativeRecognition))
        {
            throw new InvalidOperationException(
                "Recognizer returned a persisted hint without performing authoritative recognition.");
        }

        return new PageRecognitionCandidate(
            this,
            epoch,
            sequence,
            Array.AsReadOnly(strokeSnapshot),
            Array.AsReadOnly(recognized.ToArray()));
    }

    /// <summary>
    /// Gates a complete recognition snapshot, removes absent/rejected nodes, upserts every accepted line
    /// (including clean lines whose vertical position moved), and recomputes Sheet exactly once.
    /// </summary>
    public PageRecognitionApplication Apply(PageRecognitionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.AssertOwner(this);

        lock (_gate)
        {
            if (candidate.Epoch != _epoch)
            {
                throw new InvalidOperationException("The recognition candidate was invalidated by a page reset or load.");
            }
            if (candidate.Sequence <= _lastAppliedSequence)
            {
                throw new InvalidOperationException("A newer recognition candidate has already been applied.");
            }
            candidate.MarkApplied();
            _lastAppliedSequence = candidate.Sequence;

            var accepted = new Dictionary<Guid, RegionRecognition>();
            var uncertain = new HashSet<Guid>();
            foreach (RegionRecognition region in candidate.Regions)
            {
                RecognitionGate.GateResult decision =
                    RecognitionGate.Evaluate(region.Result, _minimumConfidence);
                if (!string.IsNullOrWhiteSpace(region.Result.Latex) && decision.Accepted)
                {
                    accepted[region.Region.Id] = region;
                }
                else
                {
                    uncertain.UnionWith(
                        RecognitionGate.UncertainStrokeIds(region.Result, _minimumConfidence));
                }
            }

            foreach (Guid id in _sheet.Nodes
                         .Select(node => node.Id)
                         .Where(id => !accepted.ContainsKey(id))
                         .ToArray())
            {
                _sheet.Remove(id);
            }

            RegionRecognition[] acceptedRegions = candidate.Regions
                .Where(region => accepted.ContainsKey(region.Region.Id))
                .ToArray();
            foreach (RegionRecognition region in acceptedRegions)
            {
                _sheet.Upsert(
                    region.Region.Id,
                    region.Result.Latex,
                    region.Result.Tokens,
                    region.Region.Bounds);
            }

            RecomputeReport report = _sheet.RecomputeDetailed();
            var dirtySources = candidate.Regions
                .Where(region => region.Dirty)
                .Select(region => region.Region.Id)
                .ToHashSet();
            _acceptedRegions = Array.AsReadOnly(acceptedRegions);

            return new PageRecognitionApplication(
                this,
                candidate,
                _acceptedRegions,
                uncertain,
                dirtySources,
                report);
        }
    }

    /// <summary>
    /// Makes an applied snapshot the next incremental-recognition cache only after the host has published
    /// all corresponding non-Sheet state successfully.
    /// </summary>
    public void Commit(PageRecognitionApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.AssertOwner(this);

        lock (_gate)
        {
            if (application.Candidate.Epoch != _epoch
                || application.Candidate.Sequence != _lastAppliedSequence)
            {
                throw new InvalidOperationException("The recognition application is no longer current.");
            }
            application.MarkCommitted();
            // A conforming recognizer replaces every forced persisted hint with a fresh result. Clear the
            // one-pass marker at commit as a defensive cache invariant for custom recognizer hosts.
            _previousRegions = Array.AsReadOnly(application.Regions
                .Select(region => region with { RequiresAuthoritativeRecognition = false })
                .ToArray());
        }
    }

    /// <summary>Applies and immediately commits a headless transaction with no separate presentation step.</summary>
    public PageRecognitionApplication ApplyAndCommit(PageRecognitionCandidate candidate)
    {
        PageRecognitionApplication application = Apply(candidate);
        Commit(application);
        return application;
    }

    /// <summary>
    /// Replaces the next-pass cache with persisted geometry hints. Sheet is intentionally left untouched;
    /// hints marked for authoritative recognition seed stable region identity but force the subsequent
    /// recognition transaction to reclassify and rebuild every result.
    /// </summary>
    public void ReplaceCache(IReadOnlyList<RegionRecognition> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        lock (_gate)
        {
            InvalidateCandidates();
            _previousRegions = Array.AsReadOnly(regions
                .Select(region => region with { RequiresAuthoritativeRecognition = true })
                .ToArray());
            _acceptedRegions = Array.Empty<RegionRecognition>();
        }
    }

    /// <summary>Clears Sheet and both recognition snapshots as one page reset.</summary>
    public RecomputeReport Clear()
    {
        lock (_gate)
        {
            InvalidateCandidates();
            foreach (Guid id in _sheet.Nodes.Select(node => node.Id).ToArray())
            {
                _sheet.Remove(id);
            }
            RecomputeReport report = _sheet.RecomputeDetailed();
            _previousRegions = Array.Empty<RegionRecognition>();
            _acceptedRegions = Array.Empty<RegionRecognition>();
            return report;
        }
    }

    private void InvalidateCandidates()
    {
        _epoch++;
        _nextCandidateSequence = 0;
        _lastAppliedSequence = 0;
    }
}

/// <summary>A detached recognition result awaiting latest-pass validation and transactional application.</summary>
public sealed class PageRecognitionCandidate
{
    private readonly PageRecognitionSession _owner;
    private int _applied;

    internal PageRecognitionCandidate(
        PageRecognitionSession owner,
        long epoch,
        long sequence,
        IReadOnlyList<Stroke> strokeSnapshot,
        IReadOnlyList<RegionRecognition> regions)
    {
        _owner = owner;
        Epoch = epoch;
        Sequence = sequence;
        StrokeSnapshot = strokeSnapshot;
        Regions = regions;
    }

    public IReadOnlyList<Stroke> StrokeSnapshot { get; }

    public IReadOnlyList<RegionRecognition> Regions { get; }

    internal long Epoch { get; }

    internal long Sequence { get; }

    internal void AssertOwner(PageRecognitionSession owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            throw new ArgumentException("The candidate belongs to a different page session.", nameof(owner));
        }
    }

    internal void MarkApplied()
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0)
        {
            throw new InvalidOperationException("The recognition candidate has already been applied.");
        }
    }
}

/// <summary>The complete observable outcome of one recognition-to-Sheet transaction.</summary>
public sealed class PageRecognitionApplication
{
    private readonly PageRecognitionSession _owner;
    private int _committed;

    internal PageRecognitionApplication(
        PageRecognitionSession owner,
        PageRecognitionCandidate candidate,
        IReadOnlyList<RegionRecognition> acceptedRegions,
        IReadOnlySet<Guid> uncertainStrokeIds,
        IReadOnlySet<Guid> dirtySourceRegionIds,
        RecomputeReport recomputeReport)
    {
        _owner = owner;
        Candidate = candidate;
        AcceptedRegions = acceptedRegions;
        UncertainStrokeIds = uncertainStrokeIds;
        DirtySourceRegionIds = dirtySourceRegionIds;
        RecomputeReport = recomputeReport;
    }

    public IReadOnlyList<Stroke> StrokeSnapshot => Candidate.StrokeSnapshot;

    public IReadOnlyList<RegionRecognition> Regions => Candidate.Regions;

    public IReadOnlyList<RegionRecognition> AcceptedRegions { get; }

    public IReadOnlySet<Guid> UncertainStrokeIds { get; }

    public IReadOnlySet<Guid> DirtySourceRegionIds { get; }

    public RecomputeReport RecomputeReport { get; }

    internal PageRecognitionCandidate Candidate { get; }

    internal void AssertOwner(PageRecognitionSession owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            throw new ArgumentException("The application belongs to a different page session.", nameof(owner));
        }
    }

    internal void MarkCommitted()
    {
        if (Interlocked.Exchange(ref _committed, 1) != 0)
        {
            throw new InvalidOperationException("The recognition application has already been committed.");
        }
    }
}
