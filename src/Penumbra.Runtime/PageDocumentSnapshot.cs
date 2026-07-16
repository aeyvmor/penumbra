using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;
using Penumbra.Sheet;

namespace Penumbra.Runtime;

/// <summary>Creates one immutable schema-v4 page snapshot from current ink and page cache authority.</summary>
public static class PageDocumentSnapshot
{
    /// <summary>
    /// Persists raw ink/provenance plus neutral recognition and result hints. Graph edges, roles, conflicts,
    /// and dependency authority are deliberately absent and must be rebuilt after load.
    /// </summary>
    public static PenumbraDocument Create(
        InkDocument document,
        PageRecognitionSession session)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(session);

        PersistedRegion[] regions = session.PreviousRegions.Select(region =>
        {
            SheetNode? node = session.FindNode(region.Region.Id);
            PersistedNodeResult? result = node?.Result is null ? null : new PersistedNodeResult(
                node.Result.Latex,
                node.Result.DisplayText,
                node.Result.IsComputed,
                node.Result.Kind.ToString());
            return new PersistedRegion(
                region.Region.Id,
                region.Region.StrokeIds.ToArray(),
                region.Region.Bounds,
                new PersistedRecognition(
                    region.Result.Latex,
                    region.Result.Tokens.ToArray(),
                    region.Result.Confidence,
                    region.Result.MinConfidence),
                result);
        }).ToArray();

        return document.ToDocument() with
        {
            Version = PenumbraDocumentSerializer.SchemaVersion,
            Regions = regions,
            RecognitionPipelineFingerprint = RecognitionPipelineFingerprint.Current,
        };
    }
}
