using System.Collections.ObjectModel;

namespace Penumbra.Core;

/// <summary>Stable serialized stroke-origin names used by the <c>.pen</c> contract.</summary>
public static class StrokeOriginNames
{
    public const string UserInk = "UserInk";
    public const string SynthesizedInk = "SynthesizedInk";
    public const string LegacyUnspecified = "LegacyUnspecified";
}

/// <summary>A conservative in-memory interpretation of persisted stroke provenance.</summary>
public enum StrokeOriginKind
{
    Unknown = 0,
    UserInk,
    SynthesizedInk,
    LegacyUnspecified,
}

/// <summary>Structural defects that make persisted provenance unsafe as a cache trust signal.</summary>
[Flags]
public enum StrokeProvenanceIssues
{
    None = 0,
    EmptyStrokeId = 1 << 0,
    DuplicateStrokeId = 1 << 1,
    MissingStrokeMetadata = 1 << 2,
    DuplicateStrokeMetadata = 1 << 3,
    StaleStrokeMetadata = 1 << 4,
    UnknownOrigin = 1 << 5,
    LegacyUnspecifiedOrigin = 1 << 6,
    LegacySchema = 1 << 7,
}

/// <summary>Resolved per-ID origins and their structural provenance trust state.</summary>
public sealed class StrokeProvenanceResolution
{
    internal StrokeProvenanceResolution(
        IReadOnlyDictionary<Guid, StrokeOriginKind> origins,
        StrokeProvenanceIssues issues)
    {
        Origins = origins;
        Issues = issues;
    }

    /// <summary>One conservative origin for each unique present stroke ID.</summary>
    public IReadOnlyDictionary<Guid, StrokeOriginKind> Origins { get; }

    /// <summary>All provenance defects found while resolving the document.</summary>
    public StrokeProvenanceIssues Issues { get; }

    /// <summary>
    /// Whether provenance is structurally trustworthy. A cache consumer must still independently
    /// require the expected schema version and recognition-pipeline fingerprint.
    /// </summary>
    public bool IsStructurallyTrustworthy => Issues == StrokeProvenanceIssues.None;

    /// <summary>Gets a present stroke's origin, or <see cref="StrokeOriginKind.Unknown"/>.</summary>
    public StrokeOriginKind GetOrigin(Guid strokeId) =>
        Origins.TryGetValue(strokeId, out StrokeOriginKind origin)
            ? origin
            : StrokeOriginKind.Unknown;
}

/// <summary>Resolves persisted provenance without rejecting or changing raw ink.</summary>
public static class StrokeProvenanceResolver
{
    /// <summary>Resolves every present stroke ID and reports independent structural trust.</summary>
    public static StrokeProvenanceResolution Resolve(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        Stroke[] strokes = (document.Strokes ?? Array.Empty<Stroke>())
            .Where(stroke => stroke is not null)
            .ToArray();
        PersistedStrokeMetadata[] metadata = (document.StrokeMetadata ?? Array.Empty<PersistedStrokeMetadata>())
            .Where(item => item is not null)
            .ToArray();

        IGrouping<Guid, Stroke>[] strokeGroups = strokes
            .GroupBy(stroke => stroke.Id)
            .ToArray();

        // Public consumers can load a programmatically constructed legacy DTO without passing it
        // through the serializer. Never let claimed v1-v3 metadata upgrade unknown historical ink.
        if (document.Version is >= 1 and < PenumbraDocumentSerializer.ProvenanceSchemaVersion)
        {
            return ResolveLegacy(strokeGroups);
        }

        IGrouping<Guid, PersistedStrokeMetadata>[] metadataGroups = metadata
            .GroupBy(item => item.StrokeId)
            .ToArray();
        var metadataById = metadataGroups.ToDictionary(group => group.Key);
        var presentIds = strokeGroups.Select(group => group.Key).ToHashSet();
        var origins = new Dictionary<Guid, StrokeOriginKind>();
        StrokeProvenanceIssues issues = StrokeProvenanceIssues.None;

        foreach (IGrouping<Guid, PersistedStrokeMetadata> group in metadataGroups)
        {
            if (group.Key == Guid.Empty)
            {
                issues |= StrokeProvenanceIssues.EmptyStrokeId;
            }

            if (group.Count() != 1)
            {
                issues |= StrokeProvenanceIssues.DuplicateStrokeMetadata;
            }

            if (!presentIds.Contains(group.Key))
            {
                issues |= StrokeProvenanceIssues.StaleStrokeMetadata;
            }

            foreach (PersistedStrokeMetadata item in group)
            {
                InspectOrigin(item.Origin, ref issues);
            }
        }

        foreach (IGrouping<Guid, Stroke> strokeGroup in strokeGroups)
        {
            Guid strokeId = strokeGroup.Key;
            StrokeOriginKind origin = StrokeOriginKind.Unknown;

            if (strokeId == Guid.Empty)
            {
                issues |= StrokeProvenanceIssues.EmptyStrokeId;
            }

            if (strokeGroup.Count() != 1)
            {
                issues |= StrokeProvenanceIssues.DuplicateStrokeId;
            }

            if (!metadataById.TryGetValue(strokeId, out IGrouping<Guid, PersistedStrokeMetadata>? group))
            {
                issues |= StrokeProvenanceIssues.MissingStrokeMetadata;
            }
            else if (group.Count() == 1 && strokeGroup.Count() == 1 && strokeId != Guid.Empty)
            {
                origin = ResolveOrigin(group.Single().Origin);
            }

            origins.Add(strokeId, origin);
        }

        return new StrokeProvenanceResolution(
            new ReadOnlyDictionary<Guid, StrokeOriginKind>(origins),
            issues);
    }

    private static StrokeProvenanceResolution ResolveLegacy(IGrouping<Guid, Stroke>[] strokeGroups)
    {
        var origins = new Dictionary<Guid, StrokeOriginKind>();
        StrokeProvenanceIssues issues = StrokeProvenanceIssues.LegacySchema;

        foreach (IGrouping<Guid, Stroke> strokeGroup in strokeGroups)
        {
            Guid strokeId = strokeGroup.Key;
            bool ambiguous = false;

            if (strokeId == Guid.Empty)
            {
                issues |= StrokeProvenanceIssues.EmptyStrokeId;
                ambiguous = true;
            }

            if (strokeGroup.Count() != 1)
            {
                issues |= StrokeProvenanceIssues.DuplicateStrokeId;
                ambiguous = true;
            }

            StrokeOriginKind origin = ambiguous
                ? StrokeOriginKind.Unknown
                : StrokeOriginKind.LegacyUnspecified;
            if (!ambiguous)
            {
                issues |= StrokeProvenanceIssues.LegacyUnspecifiedOrigin;
            }

            origins.Add(strokeId, origin);
        }

        return new StrokeProvenanceResolution(
            new ReadOnlyDictionary<Guid, StrokeOriginKind>(origins),
            issues);
    }

    private static StrokeOriginKind ResolveOrigin(string? origin) => origin switch
    {
        StrokeOriginNames.UserInk => StrokeOriginKind.UserInk,
        StrokeOriginNames.SynthesizedInk => StrokeOriginKind.SynthesizedInk,
        StrokeOriginNames.LegacyUnspecified => StrokeOriginKind.LegacyUnspecified,
        _ => StrokeOriginKind.Unknown,
    };

    private static void InspectOrigin(string? origin, ref StrokeProvenanceIssues issues)
    {
        if (origin == StrokeOriginNames.LegacyUnspecified)
        {
            issues |= StrokeProvenanceIssues.LegacyUnspecifiedOrigin;
        }
        else if (origin is not StrokeOriginNames.UserInk and not StrokeOriginNames.SynthesizedInk)
        {
            issues |= StrokeProvenanceIssues.UnknownOrigin;
        }
    }
}
