using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Penumbra.ExpressionCorpusHarness.Tests")]

namespace Penumbra.ExpressionCorpus;

/// <summary>Rejects untrusted runtime result shapes before the corpus runner compares them.</summary>
internal static class ActualResultContractV1
{
    public static bool IsPageValid(ActualPageV1? page)
    {
        if (page?.Regions is null
            || page.Regions.Count > CorpusResourceLimitsV1.MaximumRegionsPerPage)
        {
            return false;
        }

        var regionHandles = new HashSet<string>(StringComparer.Ordinal);
        var pageStrokeIds = new HashSet<string>(StringComparer.Ordinal);
        var pageLayoutNodes = new HashSet<ActualLayoutNodeV1>(ReferenceEqualityComparer.Instance);
        int totalRegionStrokes = 0;
        int totalTokens = 0;
        int totalLayoutNodes = 0;
        int totalLayoutEdges = 0;
        int totalLayoutOwnership = 0;

        for (int regionIndex = 0; regionIndex < page.Regions.Count; regionIndex++)
        {
            ActualRegionV1? region = page.Regions[regionIndex];
            if (region is null
                || !IsBoundedId(region.RuntimeRegionHandle)
                || !regionHandles.Add(region.RuntimeRegionHandle)
                || region.StrokeIds is null
                || region.StrokeIds.Count == 0
                || !TryConsume(
                    ref totalRegionStrokes,
                    region.StrokeIds.Count,
                    CorpusResourceLimitsV1.MaximumStrokesPerCase)
                || region.Bounds is { } bounds && !FiniteBounds(bounds))
            {
                return false;
            }

            var regionStrokeIds = new HashSet<string>(StringComparer.Ordinal);
            for (int strokeIndex = 0; strokeIndex < region.StrokeIds.Count; strokeIndex++)
            {
                string? strokeId = region.StrokeIds[strokeIndex];
                if (!IsBoundedId(strokeId)
                    || !regionStrokeIds.Add(strokeId!)
                    || !pageStrokeIds.Add(strokeId!))
                {
                    return false;
                }
            }

            switch (region.Outcome)
            {
                case AcceptedRegionActualV1 accepted:
                    if (!IsAcceptedRegionValid(
                            accepted,
                            regionStrokeIds,
                            pageLayoutNodes,
                            ref totalTokens,
                            ref totalLayoutNodes,
                            ref totalLayoutEdges,
                            ref totalLayoutOwnership))
                    {
                        return false;
                    }
                    break;
                case RefusedRegionActualV1 refused:
                    if (!CorpusRefusalSemanticsV1.IsValid(refused.FirstStage, refused.Reason))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        return IsSheetValid(page.Sheet, pageStrokeIds, regionHandles);
    }

    public static bool IsStepValid(StepActualV1? actual) => actual switch
    {
        MutationActualV1 mutation => IsDocumentStateValid(mutation.State),
        StampActualV1 stamp => IsStampValid(stamp),
        // Recognition pages are checked by IsPageValid at the comparison boundary so the
        // runner records exactly one checkpoint-level infrastructure failure.
        RecognizeActualV1 recognition => recognition.Actual is not null,
        PersistenceWriteActualV1 => true,
        PersistenceOpenActualV1 opened => IsPersistenceOpenValid(opened),
        TaffyProbeActualV1 probe => IsBoundedText(probe.TrialLatex)
            && probe.Sheet is not null
            && IsSheetValid(probe.Sheet),
        GraphActualV1 graph => IsGraphValid(graph),
        CapabilityUnavailableActualV1 unavailable => Enum.IsDefined(unavailable.Capability),
        FailedStepActualV1 failed => Enum.IsDefined(failed.Category)
            && IsBoundedId(failed.ErrorCode),
        _ => false,
    };

    private static bool IsPersistenceOpenValid(PersistenceOpenActualV1 opened)
    {
        if (!Enum.IsDefined(opened.Status) || !IsDocumentStateValid(opened.State))
        {
            return false;
        }

        return opened.Status switch
        {
            CorpusOpenStatusV1.OpenedCurrent or CorpusOpenStatusV1.BackupRecoveryCandidate =>
                opened.Page is not null && IsPageValid(opened.Page),
            CorpusOpenStatusV1.NotFound or CorpusOpenStatusV1.Invalid => opened.Page is null,
            _ => false,
        };
    }

    public static bool IsDocumentStateValid(ActualDocumentStateV1? state)
    {
        if (state?.LiveStrokeIds is null
            || state.UserInkStrokeIds is null
            || state.SynthesizedStrokeIds is null
            || state.LiveStrokeIds.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase
            || state.UserInkStrokeIds.Count > state.LiveStrokeIds.Count
            || state.SynthesizedStrokeIds.Count > state.LiveStrokeIds.Count)
        {
            return false;
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        if (!AddUniqueIds(state.LiveStrokeIds, live))
        {
            return false;
        }

        var partition = new HashSet<string>(StringComparer.Ordinal);
        if (!AddUniqueIds(state.UserInkStrokeIds, partition))
        {
            return false;
        }
        int userCount = partition.Count;
        if (!AddUniqueIds(state.SynthesizedStrokeIds, partition)
            || partition.Count != userCount + state.SynthesizedStrokeIds.Count
            || partition.Count != live.Count
            || !partition.SetEquals(live))
        {
            return false;
        }

        return true;
    }

    public static bool IsSheetValid(ActualSheetV1? sheet) => IsSheetValid(sheet, null, null);

    public static bool IsGraphValid(GraphActualV1? graph)
    {
        if (graph is null
            || !Enum.IsDefined(graph.Decision)
            || graph.Variable is not null && !IsBoundedText(graph.Variable)
            || graph.Samples is null
            || graph.Samples.Count > CorpusResourceLimitsV1.MaximumGraphSamples)
        {
            return false;
        }

        for (int index = 0; index < graph.Samples.Count; index++)
        {
            ActualGraphSampleV1? sample = graph.Samples[index];
            if (sample is null || !double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAcceptedRegionValid(
        AcceptedRegionActualV1 accepted,
        IReadOnlySet<string> regionStrokeIds,
        ISet<ActualLayoutNodeV1> pageLayoutNodes,
        ref int totalTokens,
        ref int totalLayoutNodes,
        ref int totalLayoutEdges,
        ref int totalLayoutOwnership)
    {
        if (!IsBoundedText(accepted.Latex)
            || accepted.Tokens is null
            || accepted.Tokens.Count is 0 or > CorpusResourceLimitsV1.MaximumTokensPerRegion
            || accepted.Tokens.Count > regionStrokeIds.Count
            || !TryConsume(
                ref totalTokens,
                accepted.Tokens.Count,
                CorpusResourceLimitsV1.MaximumStrokesPerCase)
            || accepted.Layout is null
            || accepted.Cas is not null && !IsEvaluationValid(accepted.Cas))
        {
            return false;
        }

        var ownedStrokes = new HashSet<string>(StringComparer.Ordinal);
        int provenanceCount = 0;
        for (int tokenIndex = 0; tokenIndex < accepted.Tokens.Count; tokenIndex++)
        {
            ActualTokenV1? token = accepted.Tokens[tokenIndex];
            if (token is null
                || !IsBoundedText(token.Latex)
                || token.SourceStrokeIds is null
                || token.SourceStrokeIds.Count == 0
                || !TryConsume(ref provenanceCount, token.SourceStrokeIds.Count, regionStrokeIds.Count)
                || !double.IsFinite(token.Confidence)
                || token.Confidence is < 0 or > 1)
            {
                return false;
            }

            for (int strokeIndex = 0; strokeIndex < token.SourceStrokeIds.Count; strokeIndex++)
            {
                string? strokeId = token.SourceStrokeIds[strokeIndex];
                if (!IsBoundedId(strokeId)
                    || !regionStrokeIds.Contains(strokeId!)
                    || !ownedStrokes.Add(strokeId!))
                {
                    return false;
                }
            }
        }

        return provenanceCount == regionStrokeIds.Count
            && ownedStrokes.Count == regionStrokeIds.Count
            && IsLayoutValid(
                accepted.Layout,
                accepted.Tokens.Count,
                pageLayoutNodes,
                ref totalLayoutNodes,
                ref totalLayoutEdges,
                ref totalLayoutOwnership);
    }

    private static bool IsLayoutValid(
        ActualLayoutNodeV1 root,
        int tokenCount,
        ISet<ActualLayoutNodeV1> pageLayoutNodes,
        ref int totalNodes,
        ref int totalEdges,
        ref int totalOwnership)
    {
        var ownedTokenIndexes = new bool[tokenCount];
        var pending = new Stack<(ActualLayoutNodeV1 Node, int Depth)>();
        pending.Push((root, 0));
        int regionOwnership = 0;

        while (pending.Count > 0)
        {
            (ActualLayoutNodeV1? node, int depth) = pending.Pop();
            if (node is null
                || depth >= CorpusJson.MaximumDepth
                || !pageLayoutNodes.Add(node)
                || !TryConsume(
                    ref totalNodes,
                    1,
                    CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion)
                || !Enum.IsDefined(node.Kind)
                || node.OwnedTokenIndexes is null
                || !TryConsume(ref regionOwnership, node.OwnedTokenIndexes.Count, tokenCount)
                || node.Children is null
                || !TryConsume(
                    ref totalEdges,
                    node.Children.Count,
                    CorpusResourceLimitsV1.MaximumLayoutNodesPerRegion))
            {
                return false;
            }

            for (int index = 0; index < node.OwnedTokenIndexes.Count; index++)
            {
                int tokenIndex = node.OwnedTokenIndexes[index];
                if (tokenIndex < 0
                    || tokenIndex >= tokenCount
                    || ownedTokenIndexes[tokenIndex])
                {
                    return false;
                }
                ownedTokenIndexes[tokenIndex] = true;
            }

            for (int edgeIndex = 0; edgeIndex < node.Children.Count; edgeIndex++)
            {
                ActualLayoutEdgeV1? edge = node.Children[edgeIndex];
                if (edge is null || !Enum.IsDefined(edge.Role) || edge.Node is null)
                {
                    return false;
                }
                pending.Push((edge.Node, depth + 1));
            }
        }

        return regionOwnership == tokenCount
            && TryConsume(
                ref totalOwnership,
                regionOwnership,
                CorpusResourceLimitsV1.MaximumStrokesPerCase);
    }

    private static bool IsSheetValid(
        ActualSheetV1? sheet,
        IReadOnlySet<string>? pageStrokeIds,
        IReadOnlySet<string>? pageRegionHandles)
    {
        if (sheet is null)
        {
            return true;
        }
        if (sheet.Nodes is null
            || sheet.Nodes.Count > CorpusResourceLimitsV1.MaximumSheetNodes
            || sheet.ChangedRegionHandles is null
            || sheet.ChangedRegionHandles.Count > CorpusResourceLimitsV1.MaximumRegionsPerPage
            || sheet.CausallyAffectedRegionHandles is null
            || sheet.CausallyAffectedRegionHandles.Count > CorpusResourceLimitsV1.MaximumRegionsPerPage
            || !UniqueBoundedIds(sheet.ChangedRegionHandles)
            || !UniqueBoundedIds(sheet.CausallyAffectedRegionHandles)
            || pageRegionHandles is not null
                && (!AllContained(sheet.ChangedRegionHandles, pageRegionHandles)
                    || !AllContained(sheet.CausallyAffectedRegionHandles, pageRegionHandles)))
        {
            return false;
        }

        var sheetStrokeIds = new HashSet<string>(StringComparer.Ordinal);
        var sheetRegionHandles = new HashSet<string>(StringComparer.Ordinal);
        int totalProvenance = 0;
        int totalFreeVariables = 0;
        for (int nodeIndex = 0; nodeIndex < sheet.Nodes.Count; nodeIndex++)
        {
            ActualSheetNodeV1? node = sheet.Nodes[nodeIndex];
            if (node is null
                || !IsBoundedId(node.RuntimeRegionHandle)
                || pageRegionHandles is not null
                    && !pageRegionHandles.Contains(node.RuntimeRegionHandle)
                || !sheetRegionHandles.Add(node.RuntimeRegionHandle)
                || node.StrokeIds is null
                || node.StrokeIds.Count == 0
                || !TryConsume(
                    ref totalProvenance,
                    node.StrokeIds.Count,
                    CorpusResourceLimitsV1.MaximumStrokesPerCase)
                || !Enum.IsDefined(node.Role)
                || (node.Role == CorpusSheetRoleV1.Definition) != (node.DefinedSymbol is not null)
                || node.DefinedSymbol is not null && !IsBoundedText(node.DefinedSymbol)
                || node.FreeVariables is null
                || !TryConsume(
                    ref totalFreeVariables,
                    node.FreeVariables.Count,
                    CorpusResourceLimitsV1.MaximumStrokesPerCase)
                || !UniqueBoundedText(node.FreeVariables)
                || node.Result is not null && !IsEvaluationValid(node.Result))
            {
                return false;
            }

            for (int strokeIndex = 0; strokeIndex < node.StrokeIds.Count; strokeIndex++)
            {
                string? strokeId = node.StrokeIds[strokeIndex];
                if (!IsBoundedId(strokeId)
                    || pageStrokeIds is not null && !pageStrokeIds.Contains(strokeId!)
                    || !sheetStrokeIds.Add(strokeId!))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsStampValid(StampActualV1 stamp)
    {
        if (!Enum.IsDefined(stamp.Decision)
            || stamp.AppliedScale is { } scale && (!double.IsFinite(scale) || scale <= 0)
            || stamp.SourceStrokes is null
            || stamp.AddedStrokes is null
            || stamp.RemovedStrokeIds is null
            || stamp.RemovedStrokeIds.Count > CorpusResourceLimitsV1.MaximumStrokesPerCase
            || !UniqueBoundedIds(stamp.RemovedStrokeIds)
            || !IsDocumentStateValid(stamp.State))
        {
            return false;
        }

        int totalStrokes = 0;
        int totalSamples = 0;
        return IsStrokeListValid(stamp.SourceStrokes, ref totalStrokes, ref totalSamples)
            && IsStrokeListValid(stamp.AddedStrokes, ref totalStrokes, ref totalSamples);
    }

    private static bool IsStrokeListValid(
        IReadOnlyList<CorpusStrokeV1> strokes,
        ref int totalStrokes,
        ref int totalSamples)
    {
        if (!TryConsume(
                ref totalStrokes,
                strokes.Count,
                CorpusResourceLimitsV1.MaximumStrokesPerCase))
        {
            return false;
        }

        var strokeIds = new HashSet<string>(StringComparer.Ordinal);
        for (int strokeIndex = 0; strokeIndex < strokes.Count; strokeIndex++)
        {
            CorpusStrokeV1? stroke = strokes[strokeIndex];
            if (stroke is null
                || !IsBoundedId(stroke.StrokeId)
                || !strokeIds.Add(stroke.StrokeId)
                || stroke.StartOffsetTicks is < 0
                || stroke.Samples is null
                || stroke.Samples.Count == 0
                || !TryConsume(
                    ref totalSamples,
                    stroke.Samples.Count,
                    CorpusResourceLimitsV1.MaximumSamplesPerCase))
            {
                return false;
            }

            long previousTicks = -1;
            for (int sampleIndex = 0; sampleIndex < stroke.Samples.Count; sampleIndex++)
            {
                CorpusSampleV1 sample = stroke.Samples[sampleIndex];
                if (!double.IsFinite(sample.X)
                    || !double.IsFinite(sample.Y)
                    || !double.IsFinite(sample.Pressure)
                    || sample.Pressure is < 0 or > 1
                    || sample.ElapsedTicks < 0
                    || sample.ElapsedTicks < previousTicks)
                {
                    return false;
                }
                previousTicks = sample.ElapsedTicks;
            }
        }
        return true;
    }

    private static bool IsEvaluationValid(ExpectedEvaluationV1 evaluation)
    {
        bool computedKind = evaluation.Kind is CorpusEvaluationKindV1.Number
            or CorpusEvaluationKindV1.Symbolic
            or CorpusEvaluationKindV1.Solution
            or CorpusEvaluationKindV1.Boolean;
        return Enum.IsDefined(evaluation.Kind)
            && evaluation.IsComputed == computedKind
            && IsBoundedText(evaluation.CanonicalValue);
    }

    private static bool AddUniqueIds(
        IReadOnlyList<string> ids,
        ISet<string> destination)
    {
        for (int index = 0; index < ids.Count; index++)
        {
            string? id = ids[index];
            if (!IsBoundedId(id) || !destination.Add(id!))
            {
                return false;
            }
        }
        return true;
    }

    private static bool UniqueBoundedIds(IReadOnlyList<string> ids)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        return AddUniqueIds(ids, unique);
    }

    private static bool UniqueBoundedText(IReadOnlyList<string> values)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < values.Count; index++)
        {
            string? value = values[index];
            if (!IsBoundedText(value) || !unique.Add(value!))
            {
                return false;
            }
        }
        return true;
    }

    private static bool AllContained(
        IReadOnlyList<string> values,
        IReadOnlySet<string> allowed)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (!allowed.Contains(values[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryConsume(ref int total, int amount, int maximum)
    {
        if (amount < 0 || total > maximum - amount)
        {
            return false;
        }
        total += amount;
        return true;
    }

    private static bool FiniteBounds(CorpusBoundsV1 bounds) =>
        double.IsFinite(bounds.X)
        && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.Width >= 0
        && bounds.Height >= 0;

    private static bool IsBoundedText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= CorpusResourceLimitsV1.MaximumTextLength;

    private static bool IsBoundedId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64;
}
