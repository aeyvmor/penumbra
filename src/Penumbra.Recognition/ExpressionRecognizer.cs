using Penumbra.Core;
using Penumbra.Core.Layout;

namespace Penumbra.Recognition;

/// <summary>
/// R1 line recognizer (Phase 3, Step 5): segments a page of strokes into symbols, classifies each in
/// the geometry context of its neighbours, and assembles the labels left-to-right into LaTeX. This
/// fully realizes Seam 1 — every <see cref="RecognizedToken"/> carries the strokes that formed it.
///
/// M1 grammar is intentionally linear (concatenate ordered tokens). 2-D structure — superscripts,
/// subscripts, fractions, a <c>\sqrt</c> covering its radicand — is the spatial-grammar follow-up.
/// </summary>
public sealed class ExpressionRecognizer : IRecognizer, IRegionRecognizer
{
    private readonly IStrokeSegmenter _segmenter;
    private readonly IRegionSegmenter _regionSegmenter;
    private readonly ISymbolClassifier _classifier;
    private readonly ILocalMetricsSink _metricsSink;
    private readonly TimeProvider _timeProvider;

    public ExpressionRecognizer(IStrokeSegmenter segmenter, ISymbolClassifier classifier)
        : this(
            segmenter,
            new RegionSegmenter(segmenter),
            classifier,
            NoOpLocalMetricsSink.Instance,
            TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates the production recognizer with an injectable local metrics sink and monotonic clock.
    /// The clock is read only while recognition CPU work is executing.
    /// </summary>
    /// <remarks>
    /// Completed processing counts dirty line-regions (zero or one for the single-result paths),
    /// partition counts resulting line-regions, classification counts symbol groups assigned to its
    /// batch, and grammar counts emitted tokens. Failed/cancelled stages omit a count except
    /// classification, whose assigned group count is already known.
    /// </remarks>
    public ExpressionRecognizer(
        IStrokeSegmenter segmenter,
        ISymbolClassifier classifier,
        ILocalMetricsSink metricsSink,
        TimeProvider? timeProvider = null)
        : this(
            segmenter,
            new RegionSegmenter(segmenter),
            classifier,
            metricsSink,
            timeProvider ?? TimeProvider.System)
    {
    }

    // Region-aware overload (Phase 5a). The region segmenter clusters the same base groups into lines,
    // then re-groups each line's strokes line-locally (see RegionSegmenter), so a region's read depends
    // only on its own ink; kept injectable so tests can supply a segmenter and inspect region ids. App
    // DI uses the full constructor below so every production seam remains independently replaceable.
    public ExpressionRecognizer(
        IStrokeSegmenter segmenter, IRegionSegmenter regionSegmenter, ISymbolClassifier classifier)
        : this(
            segmenter,
            regionSegmenter,
            classifier,
            NoOpLocalMetricsSink.Instance,
            TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates a recognizer from the complete recognition and local-observability pipeline.
    /// </summary>
    public ExpressionRecognizer(
        IStrokeSegmenter segmenter,
        IRegionSegmenter regionSegmenter,
        ISymbolClassifier classifier,
        ILocalMetricsSink metricsSink,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(segmenter);
        ArgumentNullException.ThrowIfNull(regionSegmenter);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(metricsSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _segmenter = segmenter;
        _regionSegmenter = regionSegmenter;
        _classifier = classifier;
        _metricsSink = metricsSink;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public RecognitionResult Recognize(IReadOnlyList<Stroke> strokes) =>
        Recognize(strokes, CancellationToken.None);

    /// <inheritdoc />
    /// <remarks>
    /// 4.5a: recognition is CPU-bound (segmentation + one batched ONNX call), so async means "run it
    /// on the pool with cancellation checked at stage boundaries" — a superseded live read stops
    /// before the model call instead of wasting an inference on stale ink.
    /// </remarks>
    public Task<RecognitionResult> RecognizeAsync(
        IReadOnlyList<Stroke> strokes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        return Task.Run(() => Recognize(strokes, cancellationToken), cancellationToken);
    }

    private RecognitionResult Recognize(IReadOnlyList<Stroke> strokes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        using MetricTimingScope processing = MetricTimingScope.Start(
            _metricsSink, MetricOperation.RecognitionProcessing, _timeProvider);

        try
        {
            IReadOnlyList<StrokeGroup> groups = PartitionPage(strokes, cancellationToken);
            if (groups.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var empty = new RecognitionResult(
                    string.Empty, Array.Empty<RecognizedToken>(), 0, 0);
                processing.Complete(0);
                return empty;
            }

            RecognitionResult result = RecognizeGroups(groups, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            processing.Complete(1);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            processing.Cancel();
            throw;
        }
        catch
        {
            processing.Fail();
            throw;
        }
    }

    private IReadOnlyList<StrokeGroup> PartitionPage(
        IReadOnlyList<Stroke> strokes, CancellationToken cancellationToken)
    {
        using MetricTimingScope partition = MetricTimingScope.Start(
            _metricsSink, MetricOperation.RecognitionPartition, _timeProvider);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<StrokeGroup> allGroups = _segmenter.Segment(strokes);
            cancellationToken.ThrowIfCancellationRequested();
            if (allGroups.Count == 0)
            {
                partition.Complete(0);
                return allGroups;
            }

            // Keep the legacy page guard: recognize only the largest horizontal line.
            IReadOnlyList<StrokeGroup> groups = SelectLine(allGroups);
            partition.Complete(1);
            return groups;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            partition.Cancel();
            throw;
        }
        catch
        {
            partition.Fail();
            throw;
        }
    }

    /// <summary>
    /// Recognize one already-segmented line-region (Phase 5a): the region IS the line, so there is no
    /// page-wide line-split — the <see cref="SymbolContext"/> is computed over this region's groups
    /// alone. For a single-line page this returns the same result the page-level
    /// <see cref="Recognize(IReadOnlyList{Stroke})"/> would, since it feeds the identical groups.
    /// </summary>
    public RecognitionResult RecognizeRegion(InkRegion region) =>
        RecognizeRegion(region, CancellationToken.None);

    /// <inheritdoc cref="RecognizeRegion(InkRegion)"/>
    public RecognitionResult RecognizeRegion(InkRegion region, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(region);
        using MetricTimingScope processing = MetricTimingScope.Start(
            _metricsSink, MetricOperation.RecognitionProcessing, _timeProvider);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecognitionResult result = region.Groups.Count == 0
                ? new RecognitionResult(string.Empty, Array.Empty<RecognizedToken>(), 0, 0)
                : RecognizeGroups(region.Groups, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            processing.Complete(region.Groups.Count == 0 ? 0 : 1);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            processing.Cancel();
            throw;
        }
        catch
        {
            processing.Fail();
            throw;
        }
    }

    /// <summary>
    /// Incremental multi-region read (Phase 5a). Segments <paramref name="strokes"/> into line-regions
    /// (carrying ids forward from <paramref name="previous"/> so ids stay stable across edits), then
    /// recognizes only the regions whose stroke set changed since they were last read — every clean
    /// region reuses its prior <see cref="RecognitionResult"/> untouched. Pass the returned list back in
    /// as <paramref name="previous"/> on the next edit. This is the headless core; MainWindowViewModel
    /// wires it to the canvas (increment 2) and owns the round-trip state.
    /// </summary>
    public IReadOnlyList<RegionRecognition> RecognizeRegions(
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<RegionRecognition>? previous = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        using MetricTimingScope processing = MetricTimingScope.Start(
            _metricsSink, MetricOperation.RecognitionProcessing, _timeProvider);

        try
        {
            InkSegmentation segmentation = PartitionRegions(strokes, previous, cancellationToken);

            Dictionary<Guid, RegionRecognition> priorById = previous?.ToDictionary(p => p.Region.Id)
                ?? new Dictionary<Guid, RegionRecognition>();

            var results = new List<RegionRecognition>(segmentation.Regions.Count);
            int dirtyRegionCount = 0;
            foreach (InkRegion region in segmentation.Regions)
            {
                // Check clean regions too: cancellation invalidates the whole pass even when all model
                // results would otherwise be reused from the previous round trip.
                cancellationToken.ThrowIfCancellationRequested();

                // Clean iff the matched prior region covered the exact same strokes: reuse its read verbatim.
                if (priorById.TryGetValue(region.Id, out RegionRecognition? prior)
                    && !prior.RequiresAuthoritativeRecognition
                    && region.HasSameStrokes(prior.Region))
                {
                    results.Add(new RegionRecognition(region, prior.Result, Dirty: false));
                    continue;
                }

                RecognitionResult result = RecognizeGroups(region.Groups, cancellationToken);
                results.Add(new RegionRecognition(region, result, Dirty: true));
                dirtyRegionCount++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            processing.Complete(dirtyRegionCount);
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            processing.Cancel();
            throw;
        }
        catch
        {
            processing.Fail();
            throw;
        }
    }

    private InkSegmentation PartitionRegions(
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<RegionRecognition>? previous,
        CancellationToken cancellationToken)
    {
        using MetricTimingScope partition = MetricTimingScope.Start(
            _metricsSink, MetricOperation.RecognitionPartition, _timeProvider);

        try
        {
            // Keep the existing pre-segmentation cancellation boundary inside the CPU work.
            cancellationToken.ThrowIfCancellationRequested();

            InkSegmentation? priorSegmentation = previous is null
                ? null
                : new InkSegmentation(previous.Select(p => p.Region).ToList());
            InkSegmentation segmentation = _regionSegmenter.Segment(strokes, priorSegmentation);
            cancellationToken.ThrowIfCancellationRequested();
            partition.Complete(segmentation.Regions.Count);
            return segmentation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            partition.Cancel();
            throw;
        }
        catch
        {
            partition.Fail();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<RegionRecognition>? previous = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        return Task.Run(
            () => RecognizeRegions(strokes, previous, cancellationToken),
            cancellationToken);
    }

    // Classify one line's ordered groups against a single shared context and assemble the tokens into
    // LaTeX. Shared by the page path (after SelectLine) and the per-region path so both read a line
    // identically — the only difference upstream is which groups reach here.
    private RecognitionResult RecognizeGroups(
        IReadOnlyList<StrokeGroup> groups, CancellationToken cancellationToken)
    {
        IReadOnlyList<StrokeGroup> effectiveGroups;
        IReadOnlyList<SymbolPrediction> predictions;
        using (MetricTimingScope classification = MetricTimingScope.Start(
                   _metricsSink, MetricOperation.RecognitionClassification, _timeProvider))
        {
            try
            {
                // Compute the line context once so every symbol is judged against the same neighbours —
                // this is where the geometry features finally get real siblings instead of self-as-context.
                // Computed from the ORIGINAL groups, before any radical split, so an untouched line's
                // context/refHeight is byte-for-byte what it always was.
                SymbolContext context = LineContext(groups);

                cancellationToken.ThrowIfCancellationRequested();

                (effectiveGroups, predictions) = ClassifyWithRadicalHypotheses(groups, context, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();

                classification.Complete(effectiveGroups.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                classification.Cancel(groups.Count);
                throw;
            }
            catch
            {
                classification.Fail(groups.Count);
                throw;
            }
        }

        using MetricTimingScope grammar = MetricTimingScope.Start(
            _metricsSink, MetricOperation.RecognitionGrammar, _timeProvider);
        try
        {
            // Raw (un-rewritten) Seam-1 tokens, one per classified group. The contextual glyph rewrites
            // (3.9b/3.9g's digit-context x/| rules, plus Phase 5.5's alternatives-based 0/o and 1/l
            // disambiguation) are now a grammar decision that lives inside SpatialLayoutParser's Stage 0 —
            // it always runs and always returns a rewritten token list, whatever the structural verdict.
            var rawTokens = new List<RecognizedToken>(effectiveGroups.Count);
            for (int i = 0; i < effectiveGroups.Count; i++)
            {
                StrokeGroup group = effectiveGroups[i];
                rawTokens.Add(new RecognizedToken(
                    predictions[i].Label,
                    group.Strokes.Select(s => s.Id).ToList(),
                    group.Bounds,
                    predictions[i].Confidence,
                    predictions[i].Rejected));   // B4: the OOD flag rides Seam 1 so the gate can see it
            }

            SpatialParseResult parse = SpatialLayoutParser.Parse(rawTokens, predictions);

            // An accepted tree is the recognition authority: its serialization IS the LaTeX. A refused or
            // ambiguous parse keeps the flat token-assembly LaTeX for display/debug only — the gate must
            // never let it reach the CAS (see RecognitionGate's structural refusal).
            string latex = parse.Outcome.IsAccepted
                ? LayoutLatexSerializer.Serialize(parse.Outcome.Root!)
                : TokenLatexAssembler.Assemble(parse.Tokens.Select(t => t.Latex).ToList());
            double confidenceSum = parse.Tokens.Sum(token => token.Confidence);
            double minConfidence = parse.Tokens.Min(token => token.Confidence);

            var result = new RecognitionResult(
                latex, parse.Tokens, confidenceSum / effectiveGroups.Count, minConfidence, parse.Outcome);
            cancellationToken.ThrowIfCancellationRequested();
            grammar.Complete(parse.Tokens.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            grammar.Cancel();
            throw;
        }
        catch
        {
            grammar.Fail();
            throw;
        }
    }

    /// <summary>
    /// Confidence floor for a radical-split hypothesis's leading stroke subset to count as a confident
    /// <c>\sqrt</c> read — deliberately modest (this is a genuine model classification, not a rewrite), just
    /// high enough that a low-confidence guess falls back to normal single-symbol classification rather than
    /// fragmenting a group that only geometrically resembles a radical.
    /// </summary>
    private const double RadicalConfidenceThreshold = 0.5;

    /// <summary>Hard cap for the one speculative radical batch. Hostile/tangled ink above this stroke count
    /// follows the ordinary classifier path and can still refuse structurally; it never expands speculative
    /// classification work without bound.</summary>
    private const int MaxRadicalHypothesisStrokes = 32;

    /// <summary>Hard cap for symbols produced from the speculative radicand subset.</summary>
    private const int MaxRadicalHypothesisSymbols = 32;

    /// <summary>
    /// Phase 5.5 slice 5: before the line's normal batch classify call, tries
    /// <see cref="RadicalSplitHypothesis"/> left-to-right until it finds one plausible fused group. A candidate
    /// with no plausible group is classified exactly as before — same single batch call, same count, same
    /// behaviour. The first plausible group gets ONE extra bounded batch classify call (bounded to just that
    /// hypothesis's own strokes): the leading subset alone, plus the trailing subset re-segmented into its
    /// own symbol groups. Only when the leading subset reads back as a confident,
    /// non-rejected <c>\sqrt</c> is the split committed — splicing the radical-mark group and the radicand's
    /// sub-groups into that position in reading order. <see cref="SpatialLayoutParser"/>'s existing radical
    /// consumption then independently verifies the radicand actually parses and sits within the radical's
    /// span (<see cref="ParseRefusalReason.EmptyRadicalOwnership"/> otherwise) — this method does not
    /// duplicate that check, since a hypothesis whose radical mark misreads or whose radicand fails to parse
    /// must still surface as an honest structural refusal, not a silently-dropped split. One hypothesis per
    /// expression candidate is the deliberate cost bound; a second fused radical remains a normal token and
    /// therefore refuses ownership honestly rather than triggering an unbounded classifier loop.
    /// </summary>
    private (IReadOnlyList<StrokeGroup> Groups, IReadOnlyList<SymbolPrediction> Predictions)
        ClassifyWithRadicalHypotheses(
            IReadOnlyList<StrokeGroup> groups, SymbolContext context, CancellationToken cancellationToken)
    {
        int candidateIndex = -1;
        IReadOnlyList<Stroke> candidateRadicalStrokes = Array.Empty<Stroke>();
        IReadOnlyList<Stroke> candidateRadicandStrokes = Array.Empty<Stroke>();
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i].Strokes.Count is >= 2 and <= MaxRadicalHypothesisStrokes
                && RadicalSplitHypothesis.TryFindSplit(
                    groups[i].Strokes, out IReadOnlyList<Stroke> radicalStrokes, out IReadOnlyList<Stroke> radicandStrokes))
            {
                candidateIndex = i;
                candidateRadicalStrokes = radicalStrokes;
                candidateRadicandStrokes = radicandStrokes;
                break;
            }
        }

        if (candidateIndex < 0)
        {
            return (groups, _classifier.ClassifyBatch(groups.Select(group => group.Strokes).ToList(), context));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var radicalGroup = new StrokeGroup(
            candidateRadicalStrokes, SymbolPreprocessor.Bounds(candidateRadicalStrokes));
        IReadOnlyList<StrokeGroup> radicandGroups = _segmenter.Segment(candidateRadicandStrokes);
        if (radicandGroups.Count == 0 || radicandGroups.Count > MaxRadicalHypothesisSymbols)
        {
            return (groups, _classifier.ClassifyBatch(groups.Select(group => group.Strokes).ToList(), context));
        }

        var hypothesisGroups = new List<StrokeGroup>(1 + radicandGroups.Count) { radicalGroup };
        hypothesisGroups.AddRange(radicandGroups);
        IReadOnlyList<SymbolPrediction> hypothesisPredictions = _classifier.ClassifyBatch(
            hypothesisGroups.Select(group => group.Strokes).ToList(), context);

        SymbolPrediction radicalPrediction = hypothesisPredictions[0];
        bool acceptsSplit = radicalPrediction.Label == @"\sqrt"
            && !radicalPrediction.Rejected
            && radicalPrediction.Confidence >= RadicalConfidenceThreshold;
        if (!acceptsSplit)
        {
            // One bounded hypothesis failed. Classify the untouched candidate normally; later possible
            // envelopes stay fused and the structural parser will refuse any unowned radical honestly.
            return (groups, _classifier.ClassifyBatch(groups.Select(group => group.Strokes).ToList(), context));
        }

        var normalIndices = Enumerable.Range(0, groups.Count)
            .Where(index => index != candidateIndex)
            .ToArray();
        IReadOnlyList<SymbolPrediction> normalPredictions = normalIndices.Length == 0
            ? Array.Empty<SymbolPrediction>()
            : _classifier.ClassifyBatch(normalIndices.Select(index => groups[index].Strokes).ToList(), context);

        var resultGroups = new List<StrokeGroup>(groups.Count + hypothesisGroups.Count - 1);
        var resultPredictions = new List<SymbolPrediction>(resultGroups.Capacity);
        int normalCursor = 0;
        for (int i = 0; i < groups.Count; i++)
        {
            if (i == candidateIndex)
            {
                resultGroups.AddRange(hypothesisGroups);
                resultPredictions.AddRange(hypothesisPredictions);
            }
            else
            {
                resultGroups.Add(groups[i]);
                resultPredictions.Add(normalPredictions[normalCursor]);
                normalCursor++;
            }
        }

        return (resultGroups, resultPredictions);
    }

    // The line's reference height + vertical extent, mirroring crohme.py build_split():
    // ref_h = median symbol height; expr_ymin / expr_h = the line's top edge and total height.
    private static SymbolContext LineContext(IReadOnlyList<StrokeGroup> groups)
    {
        var heights = groups.Select(g => g.Bounds.Height).OrderBy(h => h).ToList();
        int mid = heights.Count / 2;
        double median = heights.Count % 2 == 1 ? heights[mid] : (heights[mid - 1] + heights[mid]) / 2.0;
        double refHeight = median > 0 ? median : 1.0;

        double yMin = groups.Min(g => g.Bounds.Y);
        double yMax = groups.Max(g => g.Bounds.Y + g.Bounds.Height);
        return new SymbolContext(refHeight, yMin, yMax - yMin);
    }

    // 3.9f: pick the single line to recognize. Labels aren't known yet (classification runs next),
    // so we can't yet prefer "the line containing the trailing '='" — that '=' refinement is a
    // spatial-grammar follow-up. For now take the largest line (most stroke groups), tie-broken by
    // widest X-extent, and return it in left-to-right order for assembly.
    private static IReadOnlyList<StrokeGroup> SelectLine(IReadOnlyList<StrokeGroup> groups)
    {
        List<List<StrokeGroup>> lines = SplitIntoLines(groups);

        List<StrokeGroup> chosen = lines
            .OrderByDescending(line => line.Count)
            .ThenByDescending(XExtent)
            .First();

        // Restore the segmenter's reading order (left-to-right, top-to-bottom) within the line.
        return chosen.OrderBy(g => g.Bounds.X).ThenBy(g => g.Bounds.Y).ToList();
    }

    // Group stroke boxes into horizontal lines by Y-projection — shared with RegionSegmenter via
    // LineClustering so the page guard and the Phase-5a regions never drift (see LineClustering).
    private static List<List<StrokeGroup>> SplitIntoLines(IReadOnlyList<StrokeGroup> groups) =>
        LineClustering.IntoLines(groups);

    private static double XExtent(IReadOnlyList<StrokeGroup> line) =>
        line.Max(g => g.Bounds.X + g.Bounds.Width) - line.Min(g => g.Bounds.X);
}
