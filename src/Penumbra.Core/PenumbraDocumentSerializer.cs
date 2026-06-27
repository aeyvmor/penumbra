using System.Text.Json;

namespace Penumbra.Core;

/// <summary>
/// Reads and writes <see cref="PenumbraDocument"/> as JSON — the on-disk <c>.pen</c> format. Kept here in
/// Core (alongside the document model, with no UI dependency) so persistence can be unit-tested headless.
/// The document's own <see cref="PenumbraDocument.Version"/> carries the schema version for future migration.
/// </summary>
public static class PenumbraDocumentSerializer
{
    /// <summary>Current on-disk schema version. Bump when the persisted shape changes incompatibly.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>An empty page stamped with the current schema version.</summary>
    public static PenumbraDocument CreateEmpty() => new(
        Array.Empty<Stroke>(),
        Array.Empty<ExpressionNode>(),
        new Dictionary<string, string>(),
        SchemaVersion);

    /// <summary>Serializes a document to indented JSON.</summary>
    public static string Serialize(PenumbraDocument document) =>
        JsonSerializer.Serialize(document, Options);

    /// <summary>Parses a document from JSON produced by <see cref="Serialize"/>.</summary>
    public static PenumbraDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<PenumbraDocument>(json, Options)
        ?? throw new FormatException("Document JSON deserialized to null.");

    /// <summary>Writes a document to a <c>.pen</c> file.</summary>
    public static async Task SaveAsync(PenumbraDocument document, string path, CancellationToken ct = default)
    {
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, Options, ct);
    }

    /// <summary>Reads a document from a <c>.pen</c> file.</summary>
    public static async Task<PenumbraDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PenumbraDocument>(stream, Options, ct)
            ?? throw new FormatException($"Document at '{path}' deserialized to null.");
    }
}
