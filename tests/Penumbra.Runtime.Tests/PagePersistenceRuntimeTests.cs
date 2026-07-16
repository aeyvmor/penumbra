using Penumbra.Cas;
using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Runtime;
using Penumbra.Sheet;

namespace Penumbra.Runtime.Tests;

public sealed class PagePersistenceRuntimeTests
{
    [Fact]
    public async Task SnapshotAndLoadCache_PreserveNeutralV4HintsAndRejectAmbiguousAuthority()
    {
        Stroke stroke = Stroke(Guid.NewGuid(), 0);
        var document = new InkDocument();
        document.Load(PenumbraDocumentSerializer.CreateEmpty() with
        {
            Version = PenumbraDocumentSerializer.SchemaVersion,
            Strokes = [stroke],
            StrokeMetadata = [new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk)],
            RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
        });
        RegionRecognition region = Region(stroke);
        var page = new PageRecognitionSession(
            new FixedRecognizer([region]),
            new SheetGraph(new AngouriMathEvaluator(), new AngouriMathExpressionAnalyzer()),
            RecognitionCalibration.Default.MinConfidence);
        page.ApplyAndCommit(await page.RecognizeAsync(document.Strokes));

        PenumbraDocument snapshot = PageDocumentSnapshot.Create(document, page);

        Assert.Equal(PenumbraDocumentSerializer.SchemaVersion, snapshot.Version);
        Assert.Equal(RecognitionPipelineFingerprint.Current, snapshot.RecognitionPipelineFingerprint);
        Assert.Equal(StrokeOriginNames.UserInk, Assert.Single(snapshot.StrokeMetadata).Origin);
        PersistedRegion persisted = Assert.Single(snapshot.Regions);
        Assert.Equal(region.Region.Id, persisted.Id);
        Assert.Equal("1=", persisted.Recognition.Latex);
        Assert.NotNull(persisted.NodeResult);
        RegionRecognition cached = Assert.Single(PageRecognitionCache.BuildValidLoadCache(snapshot));
        Assert.False(cached.Dirty);
        Assert.True(cached.RequiresAuthoritativeRecognition);
        Assert.Equal(region.Region.Id, cached.Region.Id);
        Assert.Equal("1=", cached.Result.Latex);

        PersistedRecognition selfConsistentDisagreement = persisted.Recognition with
        {
            Latex = "7+7=",
            Tokens =
            [
                persisted.Recognition.Tokens[0] with { Latex = "7+7=" },
            ],
        };
        RegionRecognition disagreementHint = Assert.Single(PageRecognitionCache.BuildValidLoadCache(
            snapshot with
            {
                Regions = [persisted with { Recognition = selfConsistentDisagreement }],
            }));
        Assert.True(disagreementHint.RequiresAuthoritativeRecognition);
        Assert.Equal("7+7=", disagreementHint.Result.Latex);

        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with { Version = 3 }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            RecognitionPipelineFingerprint = "stale-pipeline",
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            StrokeMetadata = [new PersistedStrokeMetadata(stroke.Id, "FutureUnknownOrigin")],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Strokes = [stroke, stroke],
        }));

        PersistedRegion secondOwner = persisted with { Id = Guid.NewGuid() };
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted, secondOwner],
        }));

        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with
            {
                Recognition = persisted.Recognition with { Tokens = [] },
            }],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with
            {
                Recognition = persisted.Recognition with
                {
                    Tokens = [persisted.Recognition.Tokens[0] with { SourceStrokeIds = [] }],
                },
            }],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with
            {
                Recognition = persisted.Recognition with
                {
                    Latex = "1=1=",
                    Tokens = [persisted.Recognition.Tokens[0], persisted.Recognition.Tokens[0]],
                },
            }],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with
            {
                Recognition = persisted.Recognition with
                {
                    Confidence = double.NaN,
                    Tokens = [persisted.Recognition.Tokens[0] with { Confidence = double.NaN }],
                },
            }],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with
            {
                Recognition = persisted.Recognition with { Latex = "hostile-but-self-shaped" },
            }],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with { Bounds = new InkBounds(500, 500, 10, 10) }],
        }));
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Regions = [persisted with
            {
                Recognition = persisted.Recognition with
                {
                    Tokens = [persisted.Recognition.Tokens[0] with
                    {
                        Bounds = new InkBounds(500, 500, 10, 10),
                    }],
                },
            }],
        }));

        Guid outsideId = Guid.NewGuid();
        Stroke outside = Stroke(outsideId, 40);
        PersistedRecognition invalidRecognition = persisted.Recognition with
        {
            Tokens = [persisted.Recognition.Tokens[0] with { SourceStrokeIds = [outsideId] }],
        };
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(snapshot with
        {
            Strokes = [stroke, outside],
            StrokeMetadata =
            [
                new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk),
                new PersistedStrokeMetadata(outsideId, StrokeOriginNames.UserInk),
            ],
            Regions = [persisted with { Recognition = invalidRecognition }],
        }));
    }

    /// <summary>
    /// Phase 5.5 slice 4 cache reconciliation: an accepted spatial parse's persisted LaTeX is the tree
    /// serialization ("2\left(x+1\right)"), not the flat token assembly ("2(x+1)") — they legitimately
    /// differ for an implicit product of a digit and a delimited group. The cache must accept EITHER form
    /// (see <see cref="PageRecognitionCache"/>'s reconciliation comment) while still failing closed on a
    /// string that matches neither.
    /// </summary>
    [Fact]
    public void LoadCache_AcceptsTreeSerializedLatex_ForAnAcceptedSpatialParse()
    {
        Stroke two = Stroke(Guid.NewGuid(), 0);
        Stroke open = Stroke(Guid.NewGuid(), 20);
        Stroke x = Stroke(Guid.NewGuid(), 40);
        Stroke plus = Stroke(Guid.NewGuid(), 60);
        Stroke one = Stroke(Guid.NewGuid(), 80);
        Stroke close = Stroke(Guid.NewGuid(), 100);
        Stroke[] strokes = [two, open, x, plus, one, close];

        RecognizedToken[] tokens =
        [
            new RecognizedToken("2", [two.Id], SymbolPreprocessor.Bounds([two]), 0.9),
            new RecognizedToken("(", [open.Id], SymbolPreprocessor.Bounds([open]), 0.9),
            new RecognizedToken("x", [x.Id], SymbolPreprocessor.Bounds([x]), 0.9),
            new RecognizedToken("+", [plus.Id], SymbolPreprocessor.Bounds([plus]), 0.9),
            new RecognizedToken("1", [one.Id], SymbolPreprocessor.Bounds([one]), 0.9),
            new RecognizedToken(")", [close.Id], SymbolPreprocessor.Bounds([close]), 0.9),
        ];
        InkBounds regionBounds = SymbolPreprocessor.Bounds(strokes);
        const string treeLatex = @"2\left(x+1\right)";
        var recognition = new PersistedRecognition(treeLatex, tokens, 0.9, 0.9);
        var region = new PersistedRegion(
            Guid.NewGuid(), strokes.Select(s => s.Id).ToArray(), regionBounds, recognition, NodeResult: null);

        PenumbraDocument document = PenumbraDocumentSerializer.CreateEmpty() with
        {
            Version = PenumbraDocumentSerializer.SchemaVersion,
            Strokes = strokes,
            StrokeMetadata = strokes
                .Select(s => new PersistedStrokeMetadata(s.Id, StrokeOriginNames.UserInk))
                .ToArray(),
            RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
            Regions = [region],
        };

        RegionRecognition cached = Assert.Single(PageRecognitionCache.BuildValidLoadCache(document));
        Assert.Equal(treeLatex, cached.Result.Latex);

        // A string that matches neither the flat assembly nor any value the parser could have produced
        // still invalidates the whole cache (fail-closed).
        Assert.Empty(PageRecognitionCache.BuildValidLoadCache(document with
        {
            Regions = [region with { Recognition = recognition with { Latex = "not-a-real-value" } }],
        }));
    }

    private static RegionRecognition Region(Stroke stroke)
    {
        var bounds = new InkBounds(0, 0, 10, 10);
        RecognizedToken[] tokens =
        [
            new RecognizedToken("1=", [stroke.Id], bounds, 0.99),
        ];
        return new RegionRecognition(
            new InkRegion(
                Guid.NewGuid(),
                [stroke.Id],
                bounds,
                [new StrokeGroup([stroke], bounds)]),
            new RecognitionResult("1=", tokens, 0.99, 0.99),
            Dirty: true);
    }

    private static Stroke Stroke(Guid id, double x) => new(id,
    [
        new StrokeSample(x, 0, TimeSpan.Zero, 0.5),
        new StrokeSample(x + 10, 10, TimeSpan.FromMilliseconds(10), 0.5),
    ]);

    private sealed class FixedRecognizer(IReadOnlyList<RegionRecognition> regions) : IRegionRecognizer
    {
        public IReadOnlyList<RegionRecognition> RecognizeRegions(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => regions;

        public Task<IReadOnlyList<RegionRecognition>> RecognizeRegionsAsync(
            IReadOnlyList<Stroke> strokes,
            IReadOnlyList<RegionRecognition>? previous = null,
            CancellationToken cancellationToken = default) => Task.FromResult(regions);
    }
}
