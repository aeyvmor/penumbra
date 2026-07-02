using System.Text.Json;

namespace Penumbra.Core;

/// <summary>
/// Reads and writes <see cref="PenumbraDocument"/> as JSON — the on-disk <c>.pen</c> format. Kept here in
/// Core (alongside the document model, with no UI dependency) so persistence can be unit-tested headless.
/// The document's own <see cref="PenumbraDocument.Version"/> carries the schema version for future migration.
/// <para>
/// Schema history:
/// <list type="bullet">
///   <item><description><b>v1</b> — <see cref="Stroke.Samples"/> held the <em>smoothed</em> polyline
///   (the canvas smoothed before storing). v1 files still load as-is; their strokes stay smoothed, which
///   is acceptable for display but is why they are not first-class corpus/recognizer parity material.</description></item>
///   <item><description><b>v2</b> — <see cref="Stroke.Samples"/> holds the <em>raw</em> pen data as
///   captured; smoothing moved to render time. No field changed shape — v2 is purely a semantic change of
///   what <c>Samples</c> means. Loading a v1 file leaves its <c>Version</c> at 1 (no in-place migration).</description></item>
/// </list>
/// </para>
/// </summary>
public static class PenumbraDocumentSerializer
{
    /// <summary>
    /// Current on-disk schema version. Bump when the persisted shape or meaning changes incompatibly.
    /// v2: <see cref="Stroke.Samples"/> is raw pen data (v1 stored smoothed). See the type summary.
    /// </summary>
    public const int SchemaVersion = 2;

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
