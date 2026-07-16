using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.Runtime;

public enum PageTaffyRefusal
{
    None,
    NoActiveSession,
    InvalidLiteral,
    UnchangedValue,
    RateLimited,
}

/// <summary>One presentation-only piece of hypothetical handwriting for a taffy frame.</summary>
public sealed record PageTaffyGhost(
    Guid OwnerId,
    string ValueText,
    SynthesizedHandwriting Handwriting,
    bool IsLiteral,
    double LiftScreenPixels = 0);

/// <summary>Neutral immutable output of one taffy publication; never document or recognition input.</summary>
public sealed record PageTaffyFrame(
    IReadOnlySet<Guid> MutedStrokeIds,
    IReadOnlySet<Guid> HiddenAnswerOwnerIds,
    IReadOnlyList<PageTaffyGhost> Ghosts);

/// <summary>The outcome of one cumulative taffy move.</summary>
public sealed record PageTaffyUpdateResult(
    PageTaffyRefusal Refusal,
    string? TrialLatex,
    SheetProbeReport? ProbeReport,
    PageTaffyFrame? Frame)
{
    public bool Probed => Refusal == PageTaffyRefusal.None
        && TrialLatex is not null
        && ProbeReport is not null
        && Frame is not null;
}

/// <summary>
/// Stateful, headless taffy transaction shared by App and scenario execution. A gesture validates one
/// current numeric literal, maps cumulative screen motion from its original value, rate-limits scratch
/// Sheet probes, caches deterministic ghost geometry, and publishes immutable frames without mutating
/// document ink, committed Sheet state, recognition cache, or undo history.
/// </summary>
public sealed class PageTaffyController
{
    public static readonly TimeSpan ProbeFloor = TimeSpan.FromMilliseconds(33);
    public const double HitToleranceScreenPixels = 12;
    public const double MinimumCanvasScale = 0.1;
    public const double MaximumCanvasScale = 20;

    private readonly PageRecognitionSession _page;
    private readonly InkDocument _document;
    private readonly HandwritingSynthesizer? _synthesizer;
    private readonly TimeProvider _time;
    private readonly ILocalMetricsSink _metrics;
    private readonly Dictionary<GhostCacheKey, SynthesizedHandwriting?> _ghostCache = new();
    private ActiveSession? _session;
    private IReadOnlySet<Guid> _committedAnswerOwners = new HashSet<Guid>();

    public PageTaffyController(
        PageRecognitionSession page,
        InkDocument document,
        HandwritingSynthesizer? synthesizer,
        TimeProvider? time = null,
        ILocalMetricsSink? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(document);
        _page = page;
        _document = document;
        _synthesizer = synthesizer;
        _time = time ?? TimeProvider.System;
        _metrics = metrics ?? NoOpLocalMetricsSink.Instance;
    }

    public bool IsActive => _session is not null;

    public int ProbeCount { get; private set; }

    public PageTaffyFrame? CurrentFrame { get; private set; }

    /// <summary>Begins from an App-resolved run, revalidating it against current product state.</summary>
    public bool Begin(
        Guid ownerId,
        LiteralRun run,
        IReadOnlySet<Guid> committedAnswerOwners)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(committedAnswerOwners);
        End();

        SheetNode? node = _page.FindNode(ownerId);
        RegionRecognition? region = _page.AcceptedRegions
            .FirstOrDefault(candidate => candidate.Region.Id == ownerId);
        TaffyLiteralCandidate? candidate = region is null
            ? null
            : TaffyLiteralTree.Discover(region.Result)
                .FirstOrDefault(item => SameRun(item.Run, run));
        LiteralRun? current = candidate?.Run;
        if (node is null
            || current is null
            || current.TokenStart < 0
            || current.TokenStart + current.TokenCount > node.Tokens.Count)
        {
            return false;
        }

        HashSet<Guid> present = _document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        if (current.SourceStrokeIds.Count == 0
            || current.SourceStrokeIds.Any(id => !present.Contains(id)))
        {
            return false;
        }

        // Cache the authoritative tree target up front so Update() reserializes the tree rather than
        // rediscovering or splicing flat tokens. Non-accepted structural outcomes produce no candidates.
        TaffyLiteralLocation location = candidate!.Location;

        _ghostCache.Clear();
        ProbeCount = 0;
        _committedAnswerOwners = committedAnswerOwners.ToHashSet();
        DateTimeOffset now = _time.GetUtcNow();
        _session = new ActiveSession(
            ownerId,
            current,
            node.Tokens.ToArray(),
            current.ValueText,
            current.ValueText,
            now - ProbeFloor,
            location);
        CurrentFrame = Publish(_session, current.ValueText, report: null);
        return true;
    }

    /// <summary>
    /// Resolves the same answer-first, reverse-layer padded hit used by the canvas, then begins a validated
    /// literal gesture. This lets intent-only corpus actions exercise product hit-testing without an owner
    /// or expected value being passed to runtime.
    /// </summary>
    public bool BeginAt(
        double worldX,
        double worldY,
        double canvasScale,
        IReadOnlySet<Guid> committedAnswerOwners,
        IReadOnlyList<SynthesizedHandwriting>? answerHandwriting = null)
    {
        ArgumentNullException.ThrowIfNull(committedAnswerOwners);
        End();
        if (!double.IsFinite(worldX)
            || !double.IsFinite(worldY)
            || !double.IsFinite(canvasScale)
            || canvasScale < MinimumCanvasScale
            || canvasScale > MaximumCanvasScale)
        {
            return false;
        }

        double tolerance = HitToleranceScreenPixels / canvasScale;
        if (!double.IsFinite(tolerance))
        {
            return false;
        }
        if (answerHandwriting is not null
            && answerHandwriting.Any(answer => AnswerHitTester.HitTest(
                answer.Strokes,
                worldX,
                worldY,
                tolerance)))
        {
            return false;
        }

        LiteralHit? hit = LiteralAt(worldX, worldY, tolerance);
        return hit is not null && Begin(hit.OwnerId, hit.Run, committedAnswerOwners);
    }

    /// <summary>Applies one cumulative screen-space move; only a successful snapped probe publishes.</summary>
    public PageTaffyUpdateResult Update(double cumulativeScreenDx)
    {
        ActiveSession? session = _session;
        if (session is null)
        {
            return Refused(PageTaffyRefusal.NoActiveSession);
        }

        MetricTimingScope processing = MetricTimingScope.Start(
            _metrics,
            MetricOperation.TaffyProcessing,
            _time);
        if (!double.IsFinite(cumulativeScreenDx))
        {
            processing.Refuse();
            return Refused(PageTaffyRefusal.InvalidLiteral);
        }

        string valueText;
        try
        {
            valueText = TaffyValueMapper.Map(session.OriginalValueText, cumulativeScreenDx);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            processing.Refuse();
            return Refused(PageTaffyRefusal.InvalidLiteral);
        }
        catch
        {
            processing.Fail();
            throw;
        }

        if (string.Equals(valueText, session.LastValueText, StringComparison.Ordinal))
        {
            processing.Refuse();
            return Refused(PageTaffyRefusal.UnchangedValue);
        }

        try
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (now - session.LastProbeAt < ProbeFloor)
            {
                processing.Refuse();
                return Refused(PageTaffyRefusal.RateLimited);
            }

            string trialLatex = session.Location.Path == TaffyLiteralPath.Tree
                ? TaffyLiteralTree.BuildTrialLatex(session.Location, valueText)
                : LiteralRuns.Splice(session.Tokens, session.Run, valueText);
            SheetProbeReport report = _page.Probe(session.OwnerId, trialLatex);
            session.LastValueText = valueText;
            session.LastProbeAt = now;
            ProbeCount++;
            PageTaffyFrame frame = Publish(session, valueText, report);
            CurrentFrame = frame;
            processing.Complete(frame.Ghosts.Count);
            return new PageTaffyUpdateResult(
                PageTaffyRefusal.None,
                trialLatex,
                report,
                frame);
        }
        catch
        {
            processing.Fail();
            throw;
        }
    }

    /// <summary>Discards the hypothetical session and frame, returning whether a gesture was active.</summary>
    public bool End()
    {
        bool wasActive = _session is not null;
        _session = null;
        _ghostCache.Clear();
        _committedAnswerOwners = new HashSet<Guid>();
        CurrentFrame = null;
        return wasActive;
    }

    private PageTaffyFrame Publish(
        ActiveSession session,
        string literalValue,
        SheetProbeReport? report)
    {
        var ghosts = new List<PageTaffyGhost>();
        SynthesizedHandwriting? literal = TryBuildHandwriting(
            new GhostCacheKey(session.OwnerId, literalValue, IsLiteral: true),
            HandwritingText.FromDisplayText(literalValue),
            LiteralSpawn(session.Run, session.Tokens));
        if (literal is not null)
        {
            ghosts.Add(new PageTaffyGhost(
                session.OwnerId,
                literalValue,
                literal,
                IsLiteral: true,
                LiftScreenPixels: 10));
        }

        var hidden = new HashSet<Guid>();
        if (report is not null)
        {
            foreach (ProbeEntry entry in report.Entries)
            {
                Guid ownerId = entry.Node.Id;
                if (_committedAnswerOwners.Contains(ownerId))
                {
                    hidden.Add(ownerId);
                }
                if (!PageAnswerMaterializer.IsUsefulQueryResult(entry.Node, entry.TrialResult))
                {
                    continue;
                }

                (InkBounds Anchor, double LineHeight)? spawn =
                    PageAnswerMaterializer.FindSpawn(entry.Node.Tokens);
                if (spawn is null)
                {
                    continue;
                }

                string valueText = entry.TrialResult.DisplayText;
                SynthesizedHandwriting? answer = TryBuildHandwriting(
                    new GhostCacheKey(ownerId, valueText, IsLiteral: false),
                    HandwritingText.FromDisplayText(valueText),
                    spawn.Value);
                if (answer is not null)
                {
                    ghosts.Add(new PageTaffyGhost(
                        ownerId,
                        valueText,
                        answer,
                        IsLiteral: false));
                }
            }
        }

        MetricTimingScope publication = MetricTimingScope.Start(
            _metrics,
            MetricOperation.TaffyPublication,
            _time);
        try
        {
            var frame = new PageTaffyFrame(
                session.Run.SourceStrokeIds.ToHashSet(),
                hidden,
                ghosts);
            CurrentFrame = frame;
            publication.Complete(ghosts.Count);
            return frame;
        }
        catch
        {
            publication.Fail(ghosts.Count);
            throw;
        }
    }

    private SynthesizedHandwriting? TryBuildHandwriting(
        GhostCacheKey key,
        string text,
        (InkBounds Anchor, double LineHeight) spawn)
    {
        if (_ghostCache.TryGetValue(key, out SynthesizedHandwriting? cached))
        {
            return cached;
        }

        MetricTimingScope synthesis = MetricTimingScope.Start(
            _metrics,
            MetricOperation.TaffyGhostSynthesis,
            _time);
        SynthesizedHandwriting? synthesized;
        try
        {
            synthesized = _synthesizer?.Synthesize(
                text,
                spawn.Anchor,
                new SynthesisOptions { LineHeight = spawn.LineHeight },
                new Random(StableSeed(key)));
            int strokeCount = synthesized?.Strokes.Count ?? 0;
            if (synthesized is null || synthesized.MissingSymbols.Count > 0)
            {
                synthesis.Refuse(strokeCount);
                synthesized = null;
            }
            else
            {
                synthesis.Complete(strokeCount);
            }
        }
        catch
        {
            synthesis.Fail();
            throw;
        }

        _ghostCache[key] = synthesized;
        return synthesized;
    }

    private LiteralHit? LiteralAt(double worldX, double worldY, double tolerance)
    {
        HashSet<Guid> present = _document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        IReadOnlyList<RegionRecognition> regions = _page.AcceptedRegions;
        for (int regionIndex = regions.Count - 1; regionIndex >= 0; regionIndex--)
        {
            RegionRecognition region = regions[regionIndex];
            IReadOnlyList<TaffyLiteralCandidate> candidates = TaffyLiteralTree.Discover(region.Result);
            for (int runIndex = candidates.Count - 1; runIndex >= 0; runIndex--)
            {
                LiteralRun run = candidates[runIndex].Run;
                InkBounds bounds = run.UnionBounds;
                bool inside = worldX >= bounds.X - tolerance
                    && worldX <= bounds.X + bounds.Width + tolerance
                    && worldY >= bounds.Y - tolerance
                    && worldY <= bounds.Y + bounds.Height + tolerance;
                if (inside
                    && run.SourceStrokeIds.Count > 0
                    && run.SourceStrokeIds.All(present.Contains))
                {
                    return new LiteralHit(region.Region.Id, run);
                }
            }
        }
        return null;
    }

    private static (InkBounds Anchor, double LineHeight) LiteralSpawn(
        LiteralRun run,
        IReadOnlyList<RecognizedToken> tokens)
    {
        double lineHeight = PageAnswerMaterializer.ClampedMedianTokenHeight(tokens);
        double gap = new SynthesisOptions().GapAfterAnchor * lineHeight;
        return (
            new InkBounds(
                run.UnionBounds.X - gap,
                run.UnionBounds.Y,
                0,
                run.UnionBounds.Height),
            lineHeight);
    }

    private static bool SameRun(LiteralRun left, LiteralRun right) =>
        left.TokenStart == right.TokenStart
        && left.TokenCount == right.TokenCount
        && string.Equals(left.ValueText, right.ValueText, StringComparison.Ordinal)
        && left.SourceStrokeIds.SequenceEqual(right.SourceStrokeIds);

    // Stable within and across gestures; string.GetHashCode is intentionally process-randomized.
    private static int StableSeed(GhostCacheKey key)
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

    private static PageTaffyUpdateResult Refused(PageTaffyRefusal refusal) => new(
        refusal,
        null,
        null,
        null);

    private sealed class ActiveSession(
        Guid ownerId,
        LiteralRun run,
        IReadOnlyList<RecognizedToken> tokens,
        string originalValueText,
        string lastValueText,
        DateTimeOffset lastProbeAt,
        TaffyLiteralLocation location)
    {
        public Guid OwnerId { get; } = ownerId;
        public LiteralRun Run { get; } = run;
        public IReadOnlyList<RecognizedToken> Tokens { get; } = tokens;
        public string OriginalValueText { get; } = originalValueText;
        public string LastValueText { get; set; } = lastValueText;
        public DateTimeOffset LastProbeAt { get; set; } = lastProbeAt;
        public TaffyLiteralLocation Location { get; } = location;
    }

    private readonly record struct GhostCacheKey(Guid OwnerId, string ValueText, bool IsLiteral);
    private sealed record LiteralHit(Guid OwnerId, LiteralRun Run);
}
