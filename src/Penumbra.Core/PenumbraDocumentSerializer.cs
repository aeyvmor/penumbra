using System.Text.Json;

namespace Penumbra.Core;

/// <summary>
/// Reads and writes <see cref="PenumbraDocument"/> as JSON—the on-disk <c>.pen</c> format. Kept here in
/// Core (alongside the document model, with no UI dependency) so persistence can be unit-tested headless.
/// The document's own <see cref="PenumbraDocument.Version"/> carries the schema version for migration.
/// <para>
/// Schema history:
/// <list type="bullet">
///   <item><description><b>v1</b>—<see cref="Stroke.Samples"/> held the <em>smoothed</em> polyline.
///   These files still load as-is and retain Version 1.</description></item>
///   <item><description><b>v2</b>—<see cref="Stroke.Samples"/> holds raw captured pen data; smoothing
///   moved to render time. The persisted shape did not otherwise change.</description></item>
///   <item><description><b>v3</b>—adds neutral per-region recognition and result snapshots. Raw
///   strokes remain the source of truth; dependencies and transient rendering state are rebuilt.</description></item>
/// </list>
/// </para>
/// </summary>
public static class PenumbraDocumentSerializer
{
    /// <summary>Current on-disk schema version.</summary>
    public const int SchemaVersion = 3;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>An empty page stamped with the current schema version.</summary>
    public static PenumbraDocument CreateEmpty() => new(
        Array.Empty<Stroke>(),
        new Dictionary<string, string>(),
        SchemaVersion,
        Array.Empty<PersistedRegion>());

    /// <summary>Serializes a document to indented JSON.</summary>
    public static string Serialize(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateVersion(document.Version);
        return JsonSerializer.Serialize(Normalize(document), Options);
    }

    /// <summary>Parses and validates a supported document from JSON.</summary>
    public static PenumbraDocument Deserialize(string json)
    {
        PenumbraDocument document = JsonSerializer.Deserialize<PenumbraDocument>(json, Options)
            ?? throw new FormatException("Document JSON deserialized to null.");
        ValidateVersion(document.Version);
        return Normalize(document);
    }

    /// <summary>Writes a document to a <c>.pen</c> file.</summary>
    public static async Task SaveAsync(PenumbraDocument document, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateVersion(document.Version);
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, Normalize(document), Options, ct);
    }

    /// <summary>Reads and validates a supported document from a <c>.pen</c> file.</summary>
    public static async Task<PenumbraDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        await using FileStream stream = File.OpenRead(path);
        PenumbraDocument document = await JsonSerializer.DeserializeAsync<PenumbraDocument>(stream, Options, ct)
            ?? throw new FormatException($"Document at '{path}' deserialized to null.");
        ValidateVersion(document.Version);
        return Normalize(document);
    }

    private static void ValidateVersion(int version)
    {
        // Silently accepting a future schema could discard semantics this build does not know how to
        // reconstruct. Honest failure is safer than opening a page with plausible stale answers.
        if (version < 1 || version > SchemaVersion)
        {
            throw new NotSupportedException(
                $"Unsupported .pen schema version {version}. Supported versions are 1 through {SchemaVersion}.");
        }
    }

    private static PenumbraDocument Normalize(PenumbraDocument document)
    {
        IReadOnlyList<PersistedRegion> regions = (document.Regions ?? Array.Empty<PersistedRegion>())
            .Where(region => region is not null)
            .Select(Normalize)
            .ToArray();

        // System.Text.Json can supply null for absent collection properties even when the public
        // contract is annotated non-null. Normalize at the persistence boundary so downstream code
        // gets safe empty collections while stroke/sample values themselves stay untouched.
        return document with
        {
            Strokes = document.Strokes ?? Array.Empty<Stroke>(),
            Variables = document.Variables ?? new Dictionary<string, string>(),
            Regions = regions,
        };
    }

    private static PersistedRegion Normalize(PersistedRegion region)
    {
        PersistedRecognition recognition = region.Recognition ?? new PersistedRecognition(
            string.Empty,
            Array.Empty<RecognizedToken>(),
            0,
            0);

        recognition = recognition with
        {
            Latex = recognition.Latex ?? string.Empty,
            Tokens = (recognition.Tokens ?? Array.Empty<RecognizedToken>())
                .Where(token => token is not null)
                .Select(Normalize)
                .ToArray(),
        };

        PersistedNodeResult? result = region.NodeResult is null
            ? null
            : region.NodeResult with
            {
                Latex = region.NodeResult.Latex ?? string.Empty,
                DisplayText = region.NodeResult.DisplayText ?? string.Empty,
                Kind = region.NodeResult.Kind ?? string.Empty,
            };

        return region with
        {
            StrokeIds = region.StrokeIds ?? Array.Empty<Guid>(),
            Recognition = recognition,
            NodeResult = result,
        };
    }

    private static RecognizedToken Normalize(RecognizedToken token) => token with
    {
        Latex = token.Latex ?? string.Empty,
        SourceStrokeIds = token.SourceStrokeIds ?? Array.Empty<Guid>(),
    };
}
