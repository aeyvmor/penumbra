using Penumbra.Core;
using Penumbra.Core.Layout;
using Penumbra.Recognition;

namespace Penumbra.Runtime;

/// <summary>Validates persisted recognition hints without granting them Sheet or result authority.</summary>
public static class PageRecognitionCache
{
    /// <summary>
    /// Returns only structurally trusted v4 hints from the exact current pipeline. Invalid, legacy, stale,
    /// or ambiguous metadata yields an empty cache while leaving raw ink independently loadable. Returned
    /// hints carry a one-pass refresh marker so they can stabilize segmentation identity but never bypass
    /// authoritative recognition.
    /// </summary>
    public static IReadOnlyList<RegionRecognition> BuildValidLoadCache(PenumbraDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<PersistedRegion> persistedRegions = document.Regions ?? Array.Empty<PersistedRegion>();
        if (document.Version != PenumbraDocumentSerializer.SchemaVersion
            || !string.Equals(
                document.RecognitionPipelineFingerprint,
                RecognitionPipelineFingerprint.Current,
                StringComparison.Ordinal)
            || !StrokeProvenanceResolver.Resolve(document).IsStructurallyTrustworthy
            || persistedRegions.Count == 0)
        {
            return Array.Empty<RegionRecognition>();
        }

        IReadOnlyList<Stroke> strokes = document.Strokes ?? Array.Empty<Stroke>();
        if (strokes.Any(stroke => stroke is null || stroke.Id == Guid.Empty))
        {
            return Array.Empty<RegionRecognition>();
        }

        // Duplicate stroke ids make every reference ambiguous. Raw ink still loads and receives a fresh read.
        HashSet<Guid> ids = strokes.Select(stroke => stroke.Id).ToHashSet();
        if (ids.Count != strokes.Count)
        {
            return Array.Empty<RegionRecognition>();
        }
        Dictionary<Guid, Stroke> strokesById = strokes.ToDictionary(stroke => stroke.Id);

        if (persistedRegions.Any(region => region is null || region.StrokeIds is null))
        {
            return Array.Empty<RegionRecognition>();
        }
        Guid[] persistedRegionIds = persistedRegions.Select(region => region.Id).ToArray();
        Guid[] referencedStrokeIds = persistedRegions.SelectMany(region => region.StrokeIds).ToArray();
        if (persistedRegionIds.Any(id => id == Guid.Empty)
            || persistedRegionIds.Distinct().Count() != persistedRegionIds.Length
            || referencedStrokeIds.Distinct().Count() != referencedStrokeIds.Length)
        {
            // Regions form a partition. Ambiguous region identity/ownership invalidates the cache wholesale.
            return Array.Empty<RegionRecognition>();
        }

        var valid = new List<RegionRecognition>(persistedRegions.Count);
        foreach (PersistedRegion region in persistedRegions)
        {
            HashSet<Guid> regionIds = region.StrokeIds.ToHashSet();
            PersistedRecognition? recognition = region.Recognition;
            IReadOnlyList<RecognizedToken>? tokens = recognition?.Tokens;
            if (regionIds.Count != region.StrokeIds.Count
                || regionIds.Count == 0
                || !regionIds.All(strokesById.ContainsKey)
                || recognition is null
                || tokens is null
                || tokens.Count == 0
                || !ValidBounds(region.Bounds))
            {
                return Array.Empty<RegionRecognition>();
            }

            Stroke[] regionStrokes = region.StrokeIds.Select(id => strokesById[id]).ToArray();
            if (!ValidSourceGeometry(regionStrokes)
                || region.Bounds != SymbolPreprocessor.Bounds(regionStrokes))
            {
                return Array.Empty<RegionRecognition>();
            }

            var tokenOwners = new HashSet<Guid>();
            var labels = new string[tokens.Count];
            double confidenceSum = 0;
            double minimumConfidence = double.PositiveInfinity;
            for (int index = 0; index < tokens.Count; index++)
            {
                RecognizedToken? token = tokens[index];
                IReadOnlyList<Guid>? sourceIds = token?.SourceStrokeIds;
                if (token is null
                    || string.IsNullOrEmpty(token.Latex)
                    || sourceIds is null
                    || sourceIds.Count == 0
                    || sourceIds.Distinct().Count() != sourceIds.Count
                    || sourceIds.Any(id => !regionIds.Contains(id) || !tokenOwners.Add(id))
                    || !Probability(token.Confidence)
                    || !ValidBounds(token.Bounds))
                {
                    return Array.Empty<RegionRecognition>();
                }

                Stroke[] tokenStrokes = sourceIds.Select(id => strokesById[id]).ToArray();
                if (!ValidSourceGeometry(tokenStrokes)
                    || token.Bounds != SymbolPreprocessor.Bounds(tokenStrokes))
                {
                    return Array.Empty<RegionRecognition>();
                }

                labels[index] = token.Latex;
                confidenceSum += token.Confidence;
                minimumConfidence = Math.Min(minimumConfidence, token.Confidence);
            }

            double meanConfidence = confidenceSum / tokens.Count;
            if (!tokenOwners.SetEquals(regionIds)
                || !Probability(recognition.Confidence)
                || !Probability(recognition.MinConfidence)
                || recognition.Confidence != meanConfidence
                || recognition.MinConfidence != minimumConfidence
                || !PersistedLatexIsConsistent(recognition.Latex, tokens, labels))
            {
                return Array.Empty<RegionRecognition>();
            }

            var placeholder = new InkRegion(
                region.Id,
                region.StrokeIds.ToArray(),
                region.Bounds,
                Array.Empty<StrokeGroup>());
            var result = new RecognitionResult(
                recognition.Latex,
                tokens.ToArray(),
                recognition.Confidence,
                recognition.MinConfidence);
            // Persisted labels are hints, never recognition authority. The one-pass marker preserves
            // geometry/region identity while forcing classification and grammar on the first load pass.
            valid.Add(new RegionRecognition(
                placeholder,
                result,
                Dirty: false,
                RequiresAuthoritativeRecognition: true));
        }

        return valid;
    }

    /// <summary>
    /// Phase 5.5 slice 4 reconciliation: with the spatial grammar landed, an accepted line's persisted
    /// LaTeX is the PARSER's tree serialization, not the flat token assembly — the two can legitimately
    /// differ (e.g. an implicit product inserts no separator where the flat assembler would have). Accept
    /// EITHER form so a persisted flat-fallback string (refused/ambiguous outcomes keep the flat assembly
    /// for display/debug — see <see cref="Penumbra.Recognition.ExpressionRecognizer"/>) or a persisted
    /// tree-accepted string both validate, while any OTHER content still fails closed: re-running the exact
    /// same (fingerprint-matched — checked by the caller before this is ever reached) parser over the
    /// persisted token labels+bounds is deterministic, so a persisted string that matches neither form is
    /// simply not a value this pipeline could have produced, and the whole cache is invalidated exactly as
    /// before. Never treated as recognition authority either way — only a performance hint gated by
    /// <see cref="RegionRecognition.RequiresAuthoritativeRecognition"/>.
    /// </summary>
    private static bool PersistedLatexIsConsistent(
        string persistedLatex, IReadOnlyList<RecognizedToken> tokens, IReadOnlyList<string> labels)
    {
        if (string.Equals(persistedLatex, TokenLatexAssembler.Assemble(labels), StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var predictions = tokens
                .Select(token => new SymbolPrediction(token.Latex, token.Confidence, Rejected: token.Rejected))
                .ToList();
            SpatialParseResult parse = SpatialLayoutParser.Parse(tokens, predictions);
            return parse.Outcome.IsAccepted
                && string.Equals(
                    persistedLatex,
                    LayoutLatexSerializer.Serialize(parse.Outcome.Root!),
                    StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // A malformed persisted token shape (already checked above, but defensive) is not a value this
            // pipeline could have produced — fail closed, same as any other inconsistency.
            return false;
        }
    }

    private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool ValidBounds(InkBounds bounds) => double.IsFinite(bounds.X)
        && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width)
        && double.IsFinite(bounds.Height)
        && bounds.Width >= 0
        && bounds.Height >= 0;

    private static bool ValidSourceGeometry(IReadOnlyList<Stroke> strokes) => strokes.All(stroke =>
        stroke.Samples is { Count: > 0 }
        && stroke.Samples.All(sample => double.IsFinite(sample.X) && double.IsFinite(sample.Y)));
}
