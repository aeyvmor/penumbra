using Penumbra.Core;

namespace Penumbra.Core.Tests;

public sealed class StrokeProvenanceResolverTests
{
    [Fact]
    public void UniqueKnownOriginsAreStructurallyTrustworthy()
    {
        Stroke userStroke = CreateStroke(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Stroke synthesizedStroke = CreateStroke(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        PenumbraDocument document = CreateDocument(
            new[] { userStroke, synthesizedStroke },
            new[]
            {
                new PersistedStrokeMetadata(userStroke.Id, StrokeOriginNames.UserInk),
                new PersistedStrokeMetadata(synthesizedStroke.Id, StrokeOriginNames.SynthesizedInk),
            });

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(document);

        Assert.True(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.None, result.Issues);
        Assert.Equal(StrokeOriginKind.UserInk, result.GetOrigin(userStroke.Id));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, result.GetOrigin(synthesizedStroke.Id));
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(Guid.NewGuid()));
    }

    [Fact]
    public void MissingMetadataResolvesPresentStrokeAsUnknown()
    {
        Stroke stroke = CreateStroke(Guid.NewGuid());

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(new[] { stroke }, Array.Empty<PersistedStrokeMetadata>()));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.MissingStrokeMetadata, result.Issues);
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(stroke.Id));
    }

    [Fact]
    public void DuplicateMetadataIsAmbiguousEvenWhenBothValuesMatch()
    {
        Stroke stroke = CreateStroke(Guid.NewGuid());
        var metadata = new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk);

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(new[] { stroke }, new[] { metadata, metadata }));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.DuplicateStrokeMetadata, result.Issues);
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(stroke.Id));
    }

    [Fact]
    public void StaleMetadataInvalidatesTrustButDoesNotChangePresentOrigin()
    {
        Stroke stroke = CreateStroke(Guid.NewGuid());

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(
                new[] { stroke },
                new[]
                {
                    new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk),
                    new PersistedStrokeMetadata(Guid.NewGuid(), StrokeOriginNames.SynthesizedInk),
                }));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.StaleStrokeMetadata, result.Issues);
        Assert.Equal(StrokeOriginKind.UserInk, result.GetOrigin(stroke.Id));
        Assert.Single(result.Origins);
    }

    [Theory]
    [InlineData("FutureImportedInk")]
    [InlineData("")]
    public void UnknownOrEmptyOriginResolvesConservatively(string origin)
    {
        Stroke stroke = CreateStroke(Guid.NewGuid());

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(
                new[] { stroke },
                new[] { new PersistedStrokeMetadata(stroke.Id, origin) }));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.UnknownOrigin, result.Issues);
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(stroke.Id));
    }

    [Fact]
    public void LegacyUnspecifiedRemainsNonBankableAndStructurallyUntrusted()
    {
        Stroke stroke = CreateStroke(Guid.NewGuid());

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(
                new[] { stroke },
                new[] { new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.LegacyUnspecified) }));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.LegacyUnspecifiedOrigin, result.Issues);
        Assert.Equal(StrokeOriginKind.LegacyUnspecified, result.GetOrigin(stroke.Id));
    }

    [Fact]
    public void EmptyPresentStrokeIdResolvesConservatively()
    {
        Stroke stroke = CreateStroke(Guid.Empty);

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(
                new[] { stroke },
                new[] { new PersistedStrokeMetadata(Guid.Empty, StrokeOriginNames.UserInk) }));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.EmptyStrokeId, result.Issues);
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(Guid.Empty));
    }

    [Fact]
    public void DuplicatePresentStrokeIdsResolveAsOneUnknownOrigin()
    {
        Guid strokeId = Guid.NewGuid();

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(
            CreateDocument(
                new[] { CreateStroke(strokeId), CreateStroke(strokeId) },
                new[] { new PersistedStrokeMetadata(strokeId, StrokeOriginNames.UserInk) }));

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeProvenanceIssues.DuplicateStrokeId, result.Issues);
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(strokeId));
        Assert.Single(result.Origins);
    }

    [Fact]
    public void DirectLegacyDocumentIgnoresClaimedMetadataAtEveryLoadEntrypoint()
    {
        Guid uniqueId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid duplicateId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid staleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var document = new PenumbraDocument(
            new[]
            {
                CreateStroke(uniqueId),
                CreateStroke(duplicateId),
                CreateStroke(duplicateId),
                CreateStroke(Guid.Empty),
            },
            new Dictionary<string, string>(),
            Version: 3,
            Regions: Array.Empty<PersistedRegion>(),
            StrokeMetadata: new[]
            {
                new PersistedStrokeMetadata(uniqueId, StrokeOriginNames.UserInk),
                new PersistedStrokeMetadata(duplicateId, StrokeOriginNames.SynthesizedInk),
                new PersistedStrokeMetadata(Guid.Empty, StrokeOriginNames.UserInk),
                new PersistedStrokeMetadata(staleId, "FutureImportedInk"),
            },
            RecognitionPipelineFingerprint: "untrusted-legacy-claim");

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(document);

        Assert.False(result.IsStructurallyTrustworthy);
        Assert.Equal(
            StrokeProvenanceIssues.EmptyStrokeId
            | StrokeProvenanceIssues.DuplicateStrokeId
            | StrokeProvenanceIssues.LegacyUnspecifiedOrigin
            | StrokeProvenanceIssues.LegacySchema,
            result.Issues);
        Assert.Equal(StrokeOriginKind.LegacyUnspecified, result.GetOrigin(uniqueId));
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(duplicateId));
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(Guid.Empty));
        Assert.Equal(StrokeOriginKind.Unknown, result.GetOrigin(staleId));
    }

    [Fact]
    public void FutureSchemaDoesNotRetroactivelyMakeV4ProvenanceLegacy()
    {
        Stroke stroke = CreateStroke(Guid.NewGuid());
        PenumbraDocument document = CreateDocument(
            new[] { stroke },
            new[] { new PersistedStrokeMetadata(stroke.Id, StrokeOriginNames.UserInk) }) with
        {
            Version = PenumbraDocumentSerializer.ProvenanceSchemaVersion + 1,
        };

        StrokeProvenanceResolution result = StrokeProvenanceResolver.Resolve(document);

        // Full document/cache consumers still reject an unexpected schema version independently.
        Assert.True(result.IsStructurallyTrustworthy);
        Assert.Equal(StrokeOriginKind.UserInk, result.GetOrigin(stroke.Id));
    }

    private static Stroke CreateStroke(Guid id) => new(
        id,
        new[] { new StrokeSample(1, 2, TimeSpan.FromTicks(3), 0.5) });

    private static PenumbraDocument CreateDocument(
        IReadOnlyList<Stroke> strokes,
        IReadOnlyList<PersistedStrokeMetadata> metadata) => new(
            strokes,
            new Dictionary<string, string>(),
            PenumbraDocumentSerializer.SchemaVersion,
            Array.Empty<PersistedRegion>(),
            metadata,
            "test-pipeline-v1");
}
