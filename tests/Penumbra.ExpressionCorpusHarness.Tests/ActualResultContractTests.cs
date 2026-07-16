using System.Collections;
using Penumbra.ExpressionCorpus;

namespace Penumbra.ExpressionCorpusHarness.Tests;

public sealed class ActualResultContractTests
{
    [Fact]
    public void CompletePageShape_IsAccepted()
    {
        Assert.True(ActualResultContractV1.IsPageValid(ValidPage()));
    }

    [Fact]
    public void PageRegionHandles_MustBeUniqueAndBounded()
    {
        ActualPageV1 page = TwoRefusedRegions("stroke-a", "stroke-b") with
        {
            Regions =
            [
                RefusedRegion("same-handle", "stroke-a"),
                RefusedRegion("same-handle", "stroke-b"),
            ],
        };

        Assert.False(ActualResultContractV1.IsPageValid(page));
        Assert.False(ActualResultContractV1.IsPageValid(new ActualPageV1(
            [RefusedRegion(new string('h', 65), "stroke-a")],
            null)));
    }

    [Fact]
    public void StrokeOwnershipAcrossRegions_MustBeUnique()
    {
        ActualPageV1 page = TwoRefusedRegions("stroke-a", "stroke-a");

        Assert.False(ActualResultContractV1.IsPageValid(page));
    }

    [Fact]
    public void AcceptedTokenProvenance_MustExactlyPartitionRegionStrokes()
    {
        ActualPageV1 valid = ValidPage();
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(valid.Regions[0].Outcome);
        ActualPageV1 duplicate = ReplaceAccepted(valid, accepted with
        {
            Tokens =
            [
                accepted.Tokens[0],
                accepted.Tokens[1] with { SourceStrokeIds = ["stroke-a"] },
            ],
        });
        ActualPageV1 missing = ReplaceAccepted(valid, accepted with
        {
            Tokens = [accepted.Tokens[0]],
        });

        Assert.False(ActualResultContractV1.IsPageValid(duplicate));
        Assert.False(ActualResultContractV1.IsPageValid(missing));
    }

    [Fact]
    public void FourThousandNinetySixByFourThousandNinetySixProvenance_IsRejectedBeforeQuadraticWalk()
    {
        string[] strokeIds = Enumerable.Range(0, CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .Select(index => $"stroke-{index}")
            .ToArray();
        var countedProvenance = new CountingReadOnlyList<string>(strokeIds);
        var repeatedToken = new ActualTokenV1("x", countedProvenance, 0.99, false);
        ActualTokenV1[] tokens = Enumerable.Repeat(
                repeatedToken,
                CorpusResourceLimitsV1.MaximumTokensPerRegion)
            .ToArray();
        var layout = new ActualLayoutNodeV1(
            LayoutKindV1.Sequence,
            Enumerable.Range(0, tokens.Length).ToArray(),
            []);
        var page = new ActualPageV1(
            [
                new ActualRegionV1(
                    "region-1",
                    strokeIds,
                    new AcceptedRegionActualV1("x", tokens, layout, null),
                    new CorpusBoundsV1(0, 0, 1, 1)),
            ],
            null);

        Assert.False(ActualResultContractV1.IsPageValid(page));
        Assert.Equal(CorpusResourceLimitsV1.MaximumStrokesPerCase, countedProvenance.IndexerAccessCount);
    }

    [Fact]
    public void LayoutTokenIndexes_MustBeOwnedExactlyOnce()
    {
        ActualPageV1 valid = ValidPage();
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(valid.Regions[0].Outcome);
        var duplicateOwnership = new ActualLayoutNodeV1(
            LayoutKindV1.Sequence,
            [0],
            [
                new ActualLayoutEdgeV1(
                    LayoutRoleV1.Item,
                    new ActualLayoutNodeV1(LayoutKindV1.Token, [0], [])),
                new ActualLayoutEdgeV1(
                    LayoutRoleV1.Item,
                    new ActualLayoutNodeV1(LayoutKindV1.Token, [1], [])),
            ]);
        var missingOwnership = new ActualLayoutNodeV1(LayoutKindV1.Sequence, [0], []);

        Assert.False(ActualResultContractV1.IsPageValid(
            ReplaceAccepted(valid, accepted with { Layout = duplicateOwnership })));
        Assert.False(ActualResultContractV1.IsPageValid(
            ReplaceAccepted(valid, accepted with { Layout = missingOwnership })));
    }

    [Fact]
    public void LayoutMustBeANonSharedTree()
    {
        ActualPageV1 valid = ValidPage();
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(valid.Regions[0].Outcome);
        var shared = new ActualLayoutNodeV1(LayoutKindV1.Token, [0], []);
        var root = new ActualLayoutNodeV1(
            LayoutKindV1.Sequence,
            [1],
            [
                new ActualLayoutEdgeV1(LayoutRoleV1.Item, shared),
                new ActualLayoutEdgeV1(LayoutRoleV1.Item, shared),
            ]);

        Assert.False(ActualResultContractV1.IsPageValid(
            ReplaceAccepted(valid, accepted with { Layout = root })));

        var sharedAcrossRegions = new ActualLayoutNodeV1(LayoutKindV1.Token, [0], []);
        ActualRegionV1 FirstAcceptedRegion(string handle, string strokeId) => new(
            handle,
            [strokeId],
            new AcceptedRegionActualV1(
                "x",
                [new ActualTokenV1("x", [strokeId], 0.99, false)],
                sharedAcrossRegions,
                null),
            new CorpusBoundsV1(0, 0, 1, 1));
        Assert.False(ActualResultContractV1.IsPageValid(new ActualPageV1(
            [
                FirstAcceptedRegion("region-1", "stroke-a"),
                FirstAcceptedRegion("region-2", "stroke-b"),
            ],
            null)));
    }

    [Fact]
    public void LayoutCycle_IsRejectedWithoutRecursingForever()
    {
        ActualPageV1 valid = ValidPage();
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(valid.Regions[0].Outcome);
        var children = new List<ActualLayoutEdgeV1>();
        var root = new ActualLayoutNodeV1(LayoutKindV1.Sequence, [0, 1], children);
        children.Add(new ActualLayoutEdgeV1(LayoutRoleV1.Item, root));

        Assert.False(ActualResultContractV1.IsPageValid(
            ReplaceAccepted(valid, accepted with { Layout = root })));
    }

    [Fact]
    public void AcceptedResult_RequiresLayoutAuthority()
    {
        ActualPageV1 valid = ValidPage();
        AcceptedRegionActualV1 accepted = Assert.IsType<AcceptedRegionActualV1>(valid.Regions[0].Outcome);

        Assert.False(ActualResultContractV1.IsPageValid(
            ReplaceAccepted(valid, accepted with { Layout = null })));
    }

    [Fact]
    public void SheetStrokeProvenance_MustBeUniqueWithinAndAcrossNodes()
    {
        ActualPageV1 valid = ValidPage();
        var duplicateWithin = new ActualSheetV1(
            [SheetNode("region-1", ["stroke-a", "stroke-a"])],
            [],
            []);
        var duplicateAcross = new ActualSheetV1(
            [
                SheetNode("region-1", ["stroke-a"]),
                SheetNode("region-2", ["stroke-a"]),
            ],
            [],
            []);

        Assert.False(ActualResultContractV1.IsPageValid(valid with { Sheet = duplicateWithin }));
        Assert.False(ActualResultContractV1.IsSheetValid(duplicateAcross));
    }

    [Fact]
    public void SheetChangedAndAffectedRegionHandles_MustBeUniqueAndBounded()
    {
        var duplicateChanged = new ActualSheetV1([], ["region-1", "region-1"], []);
        var duplicateAffected = new ActualSheetV1([], [], ["region-1", "region-1"]);
        var oversized = new ActualSheetV1(
            [],
            Enumerable.Range(0, CorpusResourceLimitsV1.MaximumRegionsPerPage + 1)
                .Select(index => $"region-{index}")
                .ToArray(),
            []);

        Assert.False(ActualResultContractV1.IsSheetValid(duplicateChanged));
        Assert.False(ActualResultContractV1.IsSheetValid(duplicateAffected));
        Assert.False(ActualResultContractV1.IsSheetValid(oversized));
    }

    [Fact]
    public void SheetProvenance_HasOneCumulativePageBudget()
    {
        string[] maximum = Enumerable.Range(0, CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .Select(index => $"stroke-{index}")
            .ToArray();
        var sheet = new ActualSheetV1(
            [
                SheetNode("region-1", maximum),
                SheetNode("region-2", ["one-too-many"]),
            ],
            [],
            []);

        Assert.False(ActualResultContractV1.IsSheetValid(sheet));
    }

    [Fact]
    public void DocumentState_MustBeAnExactUniqueUserAndSynthesizedPartition()
    {
        Assert.True(ActualResultContractV1.IsDocumentStateValid(new ActualDocumentStateV1(
            ["user-a", "synth-a"],
            ["user-a"],
            ["synth-a"])));
        Assert.False(ActualResultContractV1.IsDocumentStateValid(new ActualDocumentStateV1(
            ["user-a", "synth-a"],
            ["user-a"],
            [])));
        Assert.False(ActualResultContractV1.IsDocumentStateValid(new ActualDocumentStateV1(
            ["shared"],
            ["shared"],
            ["shared"])));
        Assert.False(ActualResultContractV1.IsDocumentStateValid(new ActualDocumentStateV1(
            ["duplicate", "duplicate"],
            ["duplicate"],
            [])));
    }

    [Fact]
    public void PersistenceOpenStatus_RequiresTheMatchingPagePresence()
    {
        var state = new ActualDocumentStateV1(["stroke-a", "stroke-b"], ["stroke-a", "stroke-b"], []);
        ActualPageV1 page = ValidPage();

        Assert.True(ActualResultContractV1.IsStepValid(new PersistenceOpenActualV1(
            CorpusOpenStatusV1.OpenedCurrent,
            state,
            page)));
        Assert.True(ActualResultContractV1.IsStepValid(new PersistenceOpenActualV1(
            CorpusOpenStatusV1.BackupRecoveryCandidate,
            state,
            page)));
        Assert.True(ActualResultContractV1.IsStepValid(new PersistenceOpenActualV1(
            CorpusOpenStatusV1.NotFound,
            state,
            null)));
        Assert.True(ActualResultContractV1.IsStepValid(new PersistenceOpenActualV1(
            CorpusOpenStatusV1.Invalid,
            state,
            null)));

        Assert.False(ActualResultContractV1.IsStepValid(new PersistenceOpenActualV1(
            CorpusOpenStatusV1.OpenedCurrent,
            state,
            null)));
        Assert.False(ActualResultContractV1.IsStepValid(new PersistenceOpenActualV1(
            CorpusOpenStatusV1.NotFound,
            state,
            page)));
    }

    [Fact]
    public void GraphSamples_MustBeFiniteAndBounded()
    {
        Assert.True(ActualResultContractV1.IsGraphValid(new GraphActualV1(
            CorpusGraphDecisionV1.Graph,
            "x",
            [new ActualGraphSampleV1(0, 1)])));
        Assert.False(ActualResultContractV1.IsGraphValid(new GraphActualV1(
            CorpusGraphDecisionV1.Graph,
            "x",
            [new ActualGraphSampleV1(0, double.NaN)])));
        Assert.False(ActualResultContractV1.IsGraphValid(new GraphActualV1(
            CorpusGraphDecisionV1.Graph,
            "x",
            Enumerable.Repeat(
                    new ActualGraphSampleV1(0, 0),
                    CorpusResourceLimitsV1.MaximumGraphSamples + 1)
                .ToArray())));
    }

    [Fact]
    public void StampStrokePayload_HasOneCumulativeStrokeBudget()
    {
        CorpusStrokeV1[] source = Enumerable.Range(0, CorpusResourceLimitsV1.MaximumStrokesPerCase)
            .Select(index => Stroke($"source-{index}"))
            .ToArray();
        var stamp = new StampActualV1(
            CorpusStampDecisionV1.Replace,
            1,
            source,
            [],
            [Stroke("added")],
            new ActualDocumentStateV1([], [], []));

        Assert.False(ActualResultContractV1.IsStepValid(stamp));
    }

    [Fact]
    public void HostileNullCollections_AreRejectedWithoutThrowing()
    {
        var page = new ActualPageV1(null!, null);
        var state = new ActualDocumentStateV1(null!, [], []);
        var graph = new GraphActualV1(CorpusGraphDecisionV1.Graph, "x", null!);

        Assert.False(ActualResultContractV1.IsPageValid(page));
        Assert.False(ActualResultContractV1.IsDocumentStateValid(state));
        Assert.False(ActualResultContractV1.IsGraphValid(graph));
    }

    private static ActualPageV1 ValidPage()
    {
        var layout = new ActualLayoutNodeV1(
            LayoutKindV1.Sequence,
            [],
            [
                new ActualLayoutEdgeV1(
                    LayoutRoleV1.Item,
                    new ActualLayoutNodeV1(LayoutKindV1.Token, [0], [])),
                new ActualLayoutEdgeV1(
                    LayoutRoleV1.Item,
                    new ActualLayoutNodeV1(LayoutKindV1.Token, [1], [])),
            ]);
        return new ActualPageV1(
            [
                new ActualRegionV1(
                    "region-1",
                    ["stroke-a", "stroke-b"],
                    new AcceptedRegionActualV1(
                        "x+1",
                        [
                            new ActualTokenV1("x", ["stroke-a"], 0.99, false),
                            new ActualTokenV1("1", ["stroke-b"], 0.98, false),
                        ],
                        layout,
                        new ExpectedEvaluationV1(CorpusEvaluationKindV1.Number, true, "1")),
                    new CorpusBoundsV1(0, 0, 20, 10)),
            ],
            null);
    }

    private static ActualPageV1 ReplaceAccepted(
        ActualPageV1 page,
        AcceptedRegionActualV1 accepted) => page with
    {
        Regions = [page.Regions[0] with { Outcome = accepted }],
    };

    private static ActualPageV1 TwoRefusedRegions(string firstStrokeId, string secondStrokeId) => new(
        [
            RefusedRegion("region-1", firstStrokeId),
            RefusedRegion("region-2", secondStrokeId),
        ],
        null);

    private static ActualRegionV1 RefusedRegion(string handle, string strokeId) => new(
        handle,
        [strokeId],
        new RefusedRegionActualV1(
            CorpusFailureCategoryV1.SymbolClassification,
            CorpusRefusalCodeV1.LowConfidence),
        new CorpusBoundsV1(0, 0, 1, 1));

    private static ActualSheetNodeV1 SheetNode(
        string runtimeRegionHandle,
        IReadOnlyList<string> strokeIds) => new(
        runtimeRegionHandle,
        strokeIds,
        CorpusSheetRoleV1.Query,
        null,
        [],
        false,
        null);

    private static CorpusStrokeV1 Stroke(string id) => new(
        id,
        null,
        [new CorpusSampleV1(0, 0, 0, 0.5)]);

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int IndexerAccessCount { get; private set; }

        public int Count => inner.Count;

        public T this[int index]
        {
            get
            {
                IndexerAccessCount++;
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
