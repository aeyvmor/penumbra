namespace Penumbra.ExpressionCorpus;

internal sealed record ObservationComparison(
    bool HasExpectedObservation,
    bool ExpectedAccepted,
    bool ActualAccepted,
    bool ActualRefused,
    bool RecognitionExact,
    bool AcceptedWrong,
    bool ExpectedRefusalPass,
    bool UnexpectedRefusal,
    bool UnexpectedAcceptance,
    CorpusFailureCategoryV1? PrimaryFailure);

internal sealed record PageComparison(
    IReadOnlyList<ObservationComparison> Observations,
    IReadOnlyList<CorpusFailureCategoryV1> AdditionalFailures);

internal static class CorpusComparison
{
    public static PageComparison Compare(
        ExpectedPageV1 expected,
        ActualPageV1 actual,
        double recognitionThreshold)
    {
        var comparisons = new List<ObservationComparison>();
        var additionalFailures = new List<CorpusFailureCategoryV1>();
        var unmatchedActual = new HashSet<int>(Enumerable.Range(0, actual.Regions.Count));

        foreach (ExpectedRegionV1 expectedRegion in expected.Regions)
        {
            int match = FindExactStrokeMatch(expectedRegion.StrokeIds, actual.Regions, unmatchedActual);
            if (match < 0)
            {
                int overlap = FindLargestOverlap(expectedRegion.StrokeIds, actual.Regions, unmatchedActual);
                ActualRegionV1? candidate = overlap < 0 ? null : actual.Regions[overlap];
                if (overlap >= 0)
                {
                    unmatchedActual.Remove(overlap);
                }
                comparisons.Add(SegmentationFailure(expectedRegion, candidate, recognitionThreshold));
                continue;
            }

            unmatchedActual.Remove(match);
            comparisons.Add(CompareRegion(expectedRegion, actual.Regions[match], recognitionThreshold));
        }

        foreach (int index in unmatchedActual)
        {
            ActualRegionOutcomeV1 outcome = NormalizeOutcome(actual.Regions[index].Outcome, recognitionThreshold);
            bool accepted = outcome is AcceptedRegionActualV1;
            bool refused = outcome is RefusedRegionActualV1;
            comparisons.Add(new ObservationComparison(
                HasExpectedObservation: false,
                ExpectedAccepted: false,
                ActualAccepted: accepted,
                ActualRefused: refused,
                RecognitionExact: false,
                AcceptedWrong: accepted,
                ExpectedRefusalPass: false,
                UnexpectedRefusal: false,
                UnexpectedAcceptance: accepted,
                PrimaryFailure: CorpusFailureCategoryV1.Segmentation));
        }

        bool hasUpstreamFailure = comparisons.Any(comparison => comparison.PrimaryFailure is not null);
        if (!hasUpstreamFailure && !SheetEquals(expected, actual))
        {
            int acceptedIndex = comparisons.FindIndex(item => item.ActualAccepted);
            if (acceptedIndex >= 0)
            {
                ObservationComparison current = comparisons[acceptedIndex];
                comparisons[acceptedIndex] = current with
                {
                    AcceptedWrong = true,
                    PrimaryFailure = current.PrimaryFailure ?? CorpusFailureCategoryV1.Sheet,
                };
            }
            else
            {
                additionalFailures.Add(CorpusFailureCategoryV1.Sheet);
            }
        }

        return new PageComparison(comparisons, additionalFailures);
    }

    private static ObservationComparison CompareRegion(
        ExpectedRegionV1 expected,
        ActualRegionV1 actual,
        double recognitionThreshold)
    {
        ActualRegionOutcomeV1 actualOutcome = NormalizeOutcome(actual.Outcome, recognitionThreshold);
        if (actual.Bounds is not { } actualBounds
            || !BoundsEqual(expected.Bounds, actualBounds, expected.BoundsTolerance))
        {
            return SegmentationFailure(expected, actual, recognitionThreshold);
        }
        if (expected.Expectation is RefusedRegionExpectationV1 expectedRefusal)
        {
            if (actualOutcome is AcceptedRegionActualV1)
            {
                return new ObservationComparison(
                    HasExpectedObservation: true,
                    ExpectedAccepted: false,
                    ActualAccepted: true,
                    ActualRefused: false,
                    RecognitionExact: false,
                    AcceptedWrong: true,
                    ExpectedRefusalPass: false,
                    UnexpectedRefusal: false,
                    UnexpectedAcceptance: true,
                    PrimaryFailure: expectedRefusal.FirstStage);
            }

            var actualRefusal = (RefusedRegionActualV1)actualOutcome;
            bool exactRefusal = expectedRefusal.FirstStage == actualRefusal.FirstStage
                && expectedRefusal.Reason == actualRefusal.Reason;
            return new ObservationComparison(
                HasExpectedObservation: true,
                ExpectedAccepted: false,
                ActualAccepted: false,
                ActualRefused: true,
                RecognitionExact: false,
                AcceptedWrong: false,
                ExpectedRefusalPass: exactRefusal,
                UnexpectedRefusal: false,
                UnexpectedAcceptance: false,
                PrimaryFailure: exactRefusal ? null : actualRefusal.FirstStage);
        }

        var expectedAccepted = (AcceptedRegionExpectationV1)expected.Expectation;
        if (actualOutcome is RefusedRegionActualV1 refused)
        {
            return new ObservationComparison(
                HasExpectedObservation: true,
                ExpectedAccepted: true,
                ActualAccepted: false,
                ActualRefused: true,
                RecognitionExact: false,
                AcceptedWrong: false,
                ExpectedRefusalPass: false,
                UnexpectedRefusal: true,
                UnexpectedAcceptance: false,
                PrimaryFailure: refused.FirstStage);
        }

        var actualAccepted = (AcceptedRegionActualV1)actualOutcome;
        CorpusFailureCategoryV1? failure = FirstAcceptedFailure(expectedAccepted, actualAccepted);
        bool recognitionExact = failure is null or CorpusFailureCategoryV1.Cas or CorpusFailureCategoryV1.Sheet;
        return new ObservationComparison(
            HasExpectedObservation: true,
            ExpectedAccepted: true,
            ActualAccepted: true,
            ActualRefused: false,
            RecognitionExact: recognitionExact,
            AcceptedWrong: failure is not null,
            ExpectedRefusalPass: false,
            UnexpectedRefusal: false,
            UnexpectedAcceptance: false,
            PrimaryFailure: failure);
    }

    private static ObservationComparison SegmentationFailure(
        ExpectedRegionV1 expected,
        ActualRegionV1? actual,
        double recognitionThreshold)
    {
        ActualRegionOutcomeV1? outcome = actual is null
            ? null
            : NormalizeOutcome(actual.Outcome, recognitionThreshold);
        bool expectedAccepted = expected.Expectation is AcceptedRegionExpectationV1;
        bool actualAccepted = outcome is AcceptedRegionActualV1;
        bool actualRefused = outcome is RefusedRegionActualV1;
        return new ObservationComparison(
            HasExpectedObservation: true,
            ExpectedAccepted: expectedAccepted,
            ActualAccepted: actualAccepted,
            ActualRefused: actualRefused,
            RecognitionExact: false,
            AcceptedWrong: actualAccepted,
            ExpectedRefusalPass: false,
            UnexpectedRefusal: expectedAccepted && actualRefused,
            UnexpectedAcceptance: !expectedAccepted && actualAccepted,
            PrimaryFailure: CorpusFailureCategoryV1.Segmentation);
    }

    private static CorpusFailureCategoryV1? FirstAcceptedFailure(
        AcceptedRegionExpectationV1 expected,
        AcceptedRegionActualV1 actual)
    {
        if (!TokenGroupingEquals(expected.Tokens, actual.Tokens))
        {
            return CorpusFailureCategoryV1.Segmentation;
        }
        if (!expected.Tokens.Select(token => token.Latex)
            .SequenceEqual(actual.Tokens.Select(token => token.Latex), StringComparer.Ordinal))
        {
            return CorpusFailureCategoryV1.SymbolClassification;
        }
        if (!LayoutEquals(expected.Layout, actual.Layout, expected.Tokens))
        {
            return CorpusFailureCategoryV1.SpatialRelation;
        }
        if (!string.Equals(expected.Latex, actual.Latex, StringComparison.Ordinal))
        {
            return CorpusFailureCategoryV1.Assembly;
        }
        if (!Equals(expected.Cas, actual.Cas))
        {
            return CorpusFailureCategoryV1.Cas;
        }
        return null;
    }

    private static bool TokenGroupingEquals(
        IReadOnlyList<ExpectedTokenV1> expected,
        IReadOnlyList<ActualTokenV1> actual)
    {
        if (expected.Count != actual.Count
            || actual.Any(token => HasDuplicates(token.SourceStrokeIds))
            || HasDuplicates(actual.SelectMany(token => token.SourceStrokeIds)))
        {
            return false;
        }
        return expected.Zip(actual).All(pair => StrictSetEquals(
            pair.First.SourceStrokeIds,
            pair.Second.SourceStrokeIds));
    }

    private static bool LayoutEquals(
        ExpectedLayoutNodeV1 expected,
        ActualLayoutNodeV1? actual,
        IReadOnlyList<ExpectedTokenV1> tokens)
    {
        if (actual is null || expected.Kind != actual.Kind || expected.Children.Count != actual.Children.Count)
        {
            return false;
        }
        int[] expectedIndexes = expected.OwnedTokenIds
            .Select(id => IndexOfToken(tokens, id))
            .Order()
            .ToArray();
        if (!expectedIndexes.SequenceEqual(actual.OwnedTokenIndexes.Order()))
        {
            return false;
        }
        for (int index = 0; index < expected.Children.Count; index++)
        {
            if (expected.Children[index].Role != actual.Children[index].Role
                || !LayoutEquals(expected.Children[index].Node, actual.Children[index].Node, tokens))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SheetEquals(ExpectedPageV1 expectedPage, ActualPageV1 actualPage)
    {
        ExpectedSheetV1? expected = expectedPage.Sheet;
        ActualSheetV1? actual = actualPage.Sheet;
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }
        Dictionary<string, ActualRegionV1> actualByExpectedKey = expectedPage.Regions
            .Select(expectedRegion => (Expected: expectedRegion, Actual: actualPage.Regions.SingleOrDefault(
                actualRegion => StrictSetEquals(expectedRegion.StrokeIds, actualRegion.StrokeIds))))
            .Where(pair => pair.Actual is not null)
            .ToDictionary(pair => pair.Expected.RegionKey, pair => pair.Actual!, StringComparer.Ordinal);
        if (actualByExpectedKey.Count != expectedPage.Regions.Count
            || expected.Nodes.Count != actual.Nodes.Count
            || !TranslateHandles(expected.ChangedRegionKeys, actualByExpectedKey)
                .SequenceEqual(actual.ChangedRegionHandles, StringComparer.Ordinal)
            || !TranslateHandles(expected.CausallyAffectedRegionKeys, actualByExpectedKey)
                .SequenceEqual(actual.CausallyAffectedRegionHandles, StringComparer.Ordinal))
        {
            return false;
        }
        foreach (ExpectedSheetNodeV1 node in expected.Nodes)
        {
            ActualRegionV1 expectedRegion = actualByExpectedKey[node.RegionKey];
            ActualSheetNodeV1? match = actual.Nodes.SingleOrDefault(actualNode =>
                string.Equals(
                    expectedRegion.RuntimeRegionHandle,
                    actualNode.RuntimeRegionHandle,
                    StringComparison.Ordinal));
            if (match is null
                || !StrictSetEquals(expectedRegion.StrokeIds, match.StrokeIds)
                || node.Role != match.Role
                || !string.Equals(node.DefinedSymbol, match.DefinedSymbol, StringComparison.Ordinal)
                || !node.FreeVariables.SequenceEqual(match.FreeVariables, StringComparer.Ordinal)
                || node.IsConflict != match.IsConflict
                || !Equals(node.Result, match.Result))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool SheetEquals(
        ExpectedSheetV1 expected,
        ActualSheetV1 actual,
        IReadOnlyDictionary<string, IReadOnlyList<string>> regionStrokes,
        IReadOnlyDictionary<string, string> regionHandles)
    {
        ExpectedRegionV1[] regions = regionStrokes.Select(pair => new ExpectedRegionV1(
                pair.Key,
                pair.Value,
                default,
                0,
                new RefusedRegionExpectationV1(
                    CorpusFailureCategoryV1.SpatialRelation,
                    CorpusRefusalCodeV1.Unknown))).ToArray();
        var expectedPage = new ExpectedPageV1(regions, expected);
        ActualRegionV1[] actualRegions = regions.Select(region => new ActualRegionV1(
            regionHandles[region.RegionKey],
            region.StrokeIds,
            new RefusedRegionActualV1(
                CorpusFailureCategoryV1.SpatialRelation,
                CorpusRefusalCodeV1.Unknown))).ToArray();
        return SheetEquals(expectedPage, new ActualPageV1(actualRegions, actual));
    }

    private static IEnumerable<string> TranslateHandles(
        IEnumerable<string> regionKeys,
        IReadOnlyDictionary<string, ActualRegionV1> actualByExpectedKey) =>
        regionKeys.Select(regionKey => actualByExpectedKey[regionKey].RuntimeRegionHandle);

    private static int FindExactStrokeMatch(
        IReadOnlyList<string> expected,
        IReadOnlyList<ActualRegionV1> actual,
        IReadOnlySet<int> candidates) => candidates.FirstOrDefault(
            index => StrictSetEquals(expected, actual[index].StrokeIds),
            -1);

    private static int FindLargestOverlap(
        IReadOnlyList<string> expected,
        IReadOnlyList<ActualRegionV1> actual,
        IReadOnlySet<int> candidates)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        return candidates
            .Select(index => (Index: index, Count: actual[index].StrokeIds.Count(expectedSet.Contains)))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Index)
            .Select(item => item.Index)
            .FirstOrDefault(-1);
    }

    private static int IndexOfToken(IReadOnlyList<ExpectedTokenV1> tokens, string tokenId)
    {
        for (int index = 0; index < tokens.Count; index++)
        {
            if (string.Equals(tokens[index].TokenId, tokenId, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static ActualRegionOutcomeV1 NormalizeOutcome(
        ActualRegionOutcomeV1 outcome,
        double threshold)
    {
        if (outcome is not AcceptedRegionActualV1 accepted)
        {
            return outcome;
        }
        bool rejected = accepted.Tokens.Any(token => token.Rejected);
        bool uncertain = accepted.Tokens.Count == 0
            || accepted.Tokens.Any(token => !double.IsFinite(token.Confidence) || token.Confidence < threshold);
        if (string.IsNullOrWhiteSpace(accepted.Latex) || rejected || uncertain)
        {
            return new RefusedRegionActualV1(
                CorpusFailureCategoryV1.SymbolClassification,
                rejected ? CorpusRefusalCodeV1.OutOfDistribution : CorpusRefusalCodeV1.LowConfidence);
        }
        return accepted;
    }

    private static bool StrictSetEquals(IEnumerable<string> left, IEnumerable<string> right)
    {
        string[] leftItems = left.ToArray();
        string[] rightItems = right.ToArray();
        return !HasDuplicates(leftItems)
            && !HasDuplicates(rightItems)
            && leftItems.Length == rightItems.Length
            && leftItems.ToHashSet(StringComparer.Ordinal).SetEquals(rightItems);
    }

    private static bool HasDuplicates(IEnumerable<string> values) =>
        values.GroupBy(value => value, StringComparer.Ordinal).Any(group => group.Count() > 1);

    private static bool BoundsEqual(CorpusBoundsV1 expected, CorpusBoundsV1 actual, double tolerance) =>
        Math.Abs(expected.X - actual.X) <= tolerance
        && Math.Abs(expected.Y - actual.Y) <= tolerance
        && Math.Abs(expected.Width - actual.Width) <= tolerance
        && Math.Abs(expected.Height - actual.Height) <= tolerance;
}
