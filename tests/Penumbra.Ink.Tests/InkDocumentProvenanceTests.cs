using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class InkDocumentProvenanceTests
{
    [Fact]
    public void SingleAddDefaultsToUserInkAndAcceptsSynthesizedInkExplicitly()
    {
        var document = new InkDocument();
        Stroke user = NewStroke();
        Stroke synthesized = NewStroke();

        document.AddStroke(user);
        document.AddStroke(synthesized, StrokeOriginKind.SynthesizedInk);

        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(user.Id));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(synthesized.Id));
    }

    [Fact]
    public void BatchOriginSurvivesUndoAndRedo()
    {
        var document = new InkDocument();
        Stroke first = NewStroke();
        Stroke second = NewStroke();

        document.AddStrokes(new[] { first, second }, StrokeOriginKind.SynthesizedInk);
        document.Undo();
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(first.Id));

        document.Redo();

        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(first.Id));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(second.Id));
    }

    [Fact]
    public void ReplacementPreservesRemovedOriginAndNewOriginAcrossUndoRedo()
    {
        var document = new InkDocument();
        Stroke keep = NewStroke();
        Stroke removed = NewStroke();
        Stroke replacement = NewStroke();
        document.AddStrokes(new[] { keep, removed });

        document.ReplaceStrokes(
            new[] { removed.Id },
            new[] { replacement },
            StrokeOriginKind.SynthesizedInk);

        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(keep.Id));
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(removed.Id));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(replacement.Id));

        document.Undo();
        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(removed.Id));
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(replacement.Id));

        document.Redo();
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(removed.Id));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(replacement.Id));
    }

    [Fact]
    public void ReplacementAdditionsDefaultToUserInk()
    {
        var document = new InkDocument();
        Stroke replacement = NewStroke();

        document.ReplaceStrokes(Array.Empty<Guid>(), new[] { replacement });

        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(replacement.Id));
    }

    [Fact]
    public void EraseAndClearKeepTemporarilyAbsentStrokeOrigin()
    {
        var document = new InkDocument();
        Stroke synthesized = NewStroke();
        document.AddStroke(synthesized, StrokeOriginKind.SynthesizedInk);

        document.EraseStroke(synthesized.Id);
        document.Undo();
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(synthesized.Id));

        document.Clear();
        document.Undo();

        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(synthesized.Id));
    }

    [Fact]
    public void ReusedIdRestoresEachStrokeInstanceOriginThroughHistory()
    {
        Guid reusedId = Guid.NewGuid();
        var document = new InkDocument();
        Stroke original = NewStroke(reusedId, 1);
        Stroke replacement = NewStroke(reusedId, 10);
        document.AddStroke(original);
        document.EraseStroke(reusedId);
        document.AddStroke(replacement, StrokeOriginKind.SynthesizedInk);

        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(reusedId));

        document.Undo();
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(reusedId));
        document.Undo();
        Assert.Same(original, Assert.Single(document.Strokes));
        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(reusedId));

        document.Redo();
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(reusedId));
        document.Redo();
        Assert.Same(replacement, Assert.Single(document.Strokes));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, document.GetStrokeOrigin(reusedId));
    }

    [Fact]
    public void ReusingTheSameStrokeInstanceWithConflictingOriginsBecomesUnknown()
    {
        var document = new InkDocument();
        Stroke stroke = NewStroke();
        document.AddStroke(stroke);
        document.EraseStroke(stroke.Id);

        document.AddStroke(stroke, StrokeOriginKind.SynthesizedInk);

        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(stroke.Id));
        Assert.Equal(string.Empty, Assert.Single(document.ToDocument().StrokeMetadata).Origin);
    }

    [Fact]
    public void DuplicatePresentIdsAreAmbiguousButSnapshotRetainsPerInstanceOrigins()
    {
        Guid duplicateId = Guid.NewGuid();
        var document = new InkDocument();
        Stroke user = NewStroke(duplicateId, 1);
        Stroke synthesized = NewStroke(duplicateId, 10);
        document.AddStroke(user);
        document.AddStroke(synthesized, StrokeOriginKind.SynthesizedInk);

        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(duplicateId));

        PenumbraDocument snapshot = document.ToDocument();
        Assert.Equal(
            new[] { StrokeOriginNames.UserInk, StrokeOriginNames.SynthesizedInk },
            snapshot.StrokeMetadata.Select(metadata => metadata.Origin));
        Assert.All(snapshot.StrokeMetadata, metadata => Assert.Equal(duplicateId, metadata.StrokeId));

        var reopened = new InkDocument();
        reopened.Load(snapshot);

        Assert.Equal(2, reopened.Strokes.Count);
        Assert.Equal(StrokeOriginKind.Unknown, reopened.GetStrokeOrigin(duplicateId));
        Assert.All(reopened.ToDocument().StrokeMetadata, metadata => Assert.Equal(string.Empty, metadata.Origin));
    }

    [Fact]
    public void DirectLegacyLoadIgnoresClaimedMetadataAndResetsHistory()
    {
        Stroke old = NewStroke();
        Stroke legacy = NewStroke();
        var document = new InkDocument();
        document.AddStroke(old, StrokeOriginKind.SynthesizedInk);
        document.Undo();
        Assert.True(document.CanRedo);
        var persisted = new PenumbraDocument(
            new[] { legacy },
            new Dictionary<string, string>(),
            Version: 3,
            Regions: Array.Empty<PersistedRegion>(),
            StrokeMetadata: new[]
            {
                new PersistedStrokeMetadata(legacy.Id, StrokeOriginNames.SynthesizedInk),
            },
            RecognitionPipelineFingerprint: "untrusted-legacy-claim");

        document.Load(persisted);

        Assert.Same(legacy, Assert.Single(document.Strokes));
        Assert.Equal(StrokeOriginKind.LegacyUnspecified, document.GetStrokeOrigin(legacy.Id));
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void HostileV4MetadataNeverBlocksRawInkAndResolvesConservatively()
    {
        Stroke unknown = NewStroke();
        Stroke duplicate = NewStroke();
        Stroke valid = NewStroke();
        Stroke missing = NewStroke();
        Guid staleId = Guid.NewGuid();
        PenumbraDocument persisted = PenumbraDocumentSerializer.CreateEmpty() with
        {
            Strokes = new[] { unknown, duplicate, valid, missing },
            StrokeMetadata = new[]
            {
                new PersistedStrokeMetadata(unknown.Id, "FutureInk"),
                new PersistedStrokeMetadata(duplicate.Id, StrokeOriginNames.UserInk),
                new PersistedStrokeMetadata(duplicate.Id, StrokeOriginNames.SynthesizedInk),
                new PersistedStrokeMetadata(valid.Id, StrokeOriginNames.UserInk),
                new PersistedStrokeMetadata(staleId, StrokeOriginNames.SynthesizedInk),
            },
        };
        var document = new InkDocument();

        document.Load(persisted);

        Assert.Equal(new[] { unknown, duplicate, valid, missing }, document.Strokes);
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(unknown.Id));
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(duplicate.Id));
        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(valid.Id));
        Assert.Equal(StrokeOriginKind.Unknown, document.GetStrokeOrigin(missing.Id));
    }

    [Fact]
    public void SnapshotWritesOneMetadataEntryPerStrokeInDrawOrderWithStableNames()
    {
        Stroke legacy = NewStroke();
        Stroke unknown = NewStroke();
        var document = new InkDocument();
        document.Load(PenumbraDocumentSerializer.CreateEmpty() with
        {
            Strokes = new[] { legacy, unknown },
            StrokeMetadata = new[]
            {
                new PersistedStrokeMetadata(legacy.Id, StrokeOriginNames.LegacyUnspecified),
                new PersistedStrokeMetadata(unknown.Id, "FutureInk"),
            },
        });
        Stroke user = NewStroke();
        Stroke synthesized = NewStroke();
        document.AddStroke(user);
        document.AddStroke(synthesized, StrokeOriginKind.SynthesizedInk);

        PenumbraDocument snapshot = document.ToDocument();

        Assert.Equal(
            new[] { legacy.Id, unknown.Id, user.Id, synthesized.Id },
            snapshot.StrokeMetadata.Select(metadata => metadata.StrokeId));
        Assert.Equal(
            new[]
            {
                StrokeOriginNames.LegacyUnspecified,
                string.Empty,
                StrokeOriginNames.UserInk,
                StrokeOriginNames.SynthesizedInk,
            },
            snapshot.StrokeMetadata.Select(metadata => metadata.Origin));
    }

    [Fact]
    public void SerializedSnapshotRoundTripsWritableOrigins()
    {
        var document = new InkDocument();
        Stroke user = NewStroke();
        Stroke synthesized = NewStroke();
        document.AddStroke(user);
        document.AddStroke(synthesized, StrokeOriginKind.SynthesizedInk);

        PenumbraDocument serialized = PenumbraDocumentSerializer.Deserialize(
            PenumbraDocumentSerializer.Serialize(document.ToDocument()));
        var reopened = new InkDocument();
        reopened.Load(serialized);

        Assert.Equal(StrokeOriginKind.UserInk, reopened.GetStrokeOrigin(user.Id));
        Assert.Equal(StrokeOriginKind.SynthesizedInk, reopened.GetStrokeOrigin(synthesized.Id));
    }

    [Theory]
    [InlineData(StrokeOriginKind.Unknown)]
    [InlineData(StrokeOriginKind.LegacyUnspecified)]
    [InlineData((StrokeOriginKind)99)]
    public void NewEditOverloadsRejectNonWritableOrigins(StrokeOriginKind origin)
    {
        var document = new InkDocument();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddStroke(NewStroke(), origin));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.AddStrokes(new[] { NewStroke() }, origin));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplaceStrokes(
            Array.Empty<Guid>(),
            new[] { NewStroke() },
            origin));

        Assert.Empty(document.Strokes);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void NullBatchElementFailsAtomicallyWithoutPreRegisteringEarlierOrigins()
    {
        var document = new InkDocument();
        Stroke valid = NewStroke();

        Assert.Throws<ArgumentNullException>(() => document.AddStrokes(
            new Stroke[] { valid, null! },
            StrokeOriginKind.SynthesizedInk));

        Assert.Empty(document.Strokes);
        Assert.False(document.CanUndo);
        document.AddStroke(valid);
        Assert.Equal(StrokeOriginKind.UserInk, document.GetStrokeOrigin(valid.Id));
    }

    private static Stroke NewStroke(Guid? id = null, double offset = 0) => new(
        id ?? Guid.NewGuid(),
        new[]
        {
            new StrokeSample(offset, 0, TimeSpan.Zero, 0.5),
            new StrokeSample(offset + 1, 1, TimeSpan.FromMilliseconds(10), 0.5),
        });
}
