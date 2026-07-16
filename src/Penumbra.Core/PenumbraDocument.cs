namespace Penumbra.Core;

/// <summary>The persisted Penumbra page model.</summary>
/// <remarks>
/// Earlier schemas carried an always-empty <c>Expressions</c> list (the placeholder
/// <c>ExpressionNode</c>, removed in Phase 5 — Seam 2's node now lives in <c>Penumbra.Sheet</c> as
/// <c>SheetNode</c>). Old files with <c>"Expressions": []</c> still load: the unknown property is
/// ignored. Schema v3 stores neutral recognition and result snapshots, but deliberately does not
/// persist dependency edges or other derived Sheet state. Rebuilding those facts through the Sheet
/// API on load prevents an old cache from becoming an authoritative—and potentially wrong—graph.
/// Schema v4 adds document-level stroke provenance and a recognition-pipeline fingerprint while raw
/// <see cref="Stroke"/> geometry remains unchanged.
/// </remarks>
public sealed record PenumbraDocument(
    IReadOnlyList<Stroke> Strokes,
    IReadOnlyDictionary<string, string> Variables,
    int Version,
    IReadOnlyList<PersistedRegion> Regions = null!,
    IReadOnlyList<PersistedStrokeMetadata> StrokeMetadata = null!,
    string RecognitionPipelineFingerprint = "");

/// <summary>Document-level provenance for one persisted stroke ID.</summary>
/// <remarks>
/// <see cref="Origin"/> is intentionally a string so newer producers can add origin values without
/// making older readers reject otherwise valid raw ink. Consumers resolve unknown or ambiguous values
/// conservatively through <see cref="StrokeProvenanceResolver"/>.
/// </remarks>
public sealed record PersistedStrokeMetadata(Guid StrokeId, string Origin);

/// <summary>
/// A stable recognition-region snapshot stored by schema v3. The types live in Core so the on-disk
/// contract remains independent of Recognition, Sheet, CAS, Ink, and UI implementations.
/// </summary>
/// <remarks>
/// <see cref="StrokeIds"/> identifies the raw document strokes that produced this region. Consumers
/// must validate those references against the loaded page before using the snapshot as recognition
/// cache input; malformed cache data must never prevent the raw ink itself from loading.
/// </remarks>
public sealed record PersistedRegion(
    Guid Id,
    IReadOnlyList<Guid> StrokeIds,
    InkBounds Bounds,
    PersistedRecognition Recognition,
    PersistedNodeResult? NodeResult = null);

/// <summary>
/// Neutral recognition data for a persisted region. Tokens retain Seam 1's source-stroke alignment;
/// confidence values are cache metadata and remain subject to the current recognition gate on load.
/// </summary>
public sealed record PersistedRecognition(
    string Latex,
    IReadOnlyList<RecognizedToken> Tokens,
    double Confidence,
    double MinConfidence);

/// <summary>
/// Optional cached presentation result for a region. <see cref="Kind"/> is intentionally a string:
/// serializing a Sheet/CAS enum here would reverse the dependency direction and freeze an engine type
/// into the file format. The reconstructed graph, not this snapshot, is authoritative.
/// </summary>
public sealed record PersistedNodeResult(
    string Latex,
    string DisplayText,
    bool IsComputed,
    string Kind);
