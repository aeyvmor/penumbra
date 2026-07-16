using Penumbra.Core;

namespace Penumbra.Recognition;

/// <summary>
/// Phase 5.5 slice 5: pure geometric detection of the "fused radical + radicand" shape a single-symbol
/// segmenter pass produces (the pinned <c>SegmenterRealInkRegressionTests.Shot2_Sqrt9Equals_...</c> fixture —
/// the radical tick, rise, and vinculum strokes plus the radicand's own strokes all merge into one
/// <see cref="StrokeGroup"/> because the vinculum's bounding box touches the radicand's). Given that one
/// group's strokes, finds the SMALLEST leading-by-X subset whose combined bounds start at least as far
/// left/up as the rest AND stretch a bar across nearly the rest's whole width — the shape of a hand-drawn
/// radical sign sitting to the left of, and spanning a bar over, its radicand.
/// <para>
/// This is a geometry-only hypothesis: it never touches a classifier. <see cref="ExpressionRecognizer"/>
/// is the one place that both owns the model and the recursive parser, so it is the one that verifies the
/// guess (an extra bounded classify call for "does the leading subset actually read as <c>\sqrt</c>", plus
/// a recursive parse of the remaining strokes as the candidate radicand) and only commits to the split when
/// both hold. A group whose shape does not match this hypothesis — most groups — is untouched and flows
/// through classification exactly as before.
/// </para>
/// </summary>
internal static class RadicalSplitHypothesis
{
    /// <summary>Vertical tolerance (as a fraction of the radicand candidate's own height) for the radical
    /// candidate's top edge to sit at or above the radicand candidate's top edge — real ink rarely aligns
    /// exactly.</summary>
    private const double TopToleranceRatio = 0.15;

    /// <summary>Minimum fraction of the radicand candidate's width the radical candidate's combined bounds
    /// must reach — a vinculum is drawn to stretch nearly all the way across what it covers.</summary>
    private const double MinBarCoverageRatio = 0.9;

    /// <summary>A genuine radical envelope has meaningful horizontal reach relative to its height; a
    /// descending letter such as <c>y</c> can satisfy the coverage test but remains too narrow.</summary>
    private const double MinEnvelopeWidthToHeightRatio = 0.75;

    /// <summary>The radical hook must begin materially left of the covered ink. Parallel equals bars and
    /// ordinary multi-stroke letters usually begin at the same X and are not structural envelopes.</summary>
    private const double MinLeftLeadHeightRatio = 0.15;

    /// <summary>The proposed radical subset must have real vertical body relative to the radicand; this
    /// excludes two flat equals bars before classification spends the one bounded hypothesis.</summary>
    private const double MinEnvelopeToRadicandHeightRatio = 0.5;

    /// <summary>
    /// Attempts to split <paramref name="strokes"/> into a leading "radical mark" subset and a trailing
    /// "radicand" subset. Returns false (and empty lists) when no split satisfies the envelope/coverage
    /// shape — the overwhelming majority of groups, which is the safe default (they are left to classify
    /// normally as a single symbol).
    /// </summary>
    public static bool TryFindSplit(
        IReadOnlyList<Stroke> strokes,
        out IReadOnlyList<Stroke> radicalStrokes,
        out IReadOnlyList<Stroke> radicandStrokes)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        radicalStrokes = Array.Empty<Stroke>();
        radicandStrokes = Array.Empty<Stroke>();

        if (strokes.Count < 2)
        {
            return false;
        }

        List<Stroke> ordered = strokes
            .OrderBy(s => SymbolPreprocessor.Bounds(new[] { s }).X)
            .ThenBy(s => SymbolPreprocessor.Bounds(new[] { s }).Y)
            .ToList();

        for (int k = 1; k < ordered.Count; k++)
        {
            List<Stroke> leading = ordered.Take(k).ToList();
            List<Stroke> trailing = ordered.Skip(k).ToList();
            InkBounds leadingBounds = SymbolPreprocessor.Bounds(leading);
            InkBounds trailingBounds = SymbolPreprocessor.Bounds(trailing);

            double tolerance = TopToleranceRatio * Math.Max(trailingBounds.Height, 1.0);
            bool startsAtOrAboveTop = leadingBounds.Y <= trailingBounds.Y + tolerance;
            bool startsAtOrLeftOfLeft = leadingBounds.X <= trailingBounds.X;
            bool spansAcrossTheRest = leadingBounds.Width >= trailingBounds.Width * MinBarCoverageRatio;
            double envelopeHeight = Math.Max(leadingBounds.Height, 1.0);
            bool hasEnvelopeAspect = leadingBounds.Width
                >= envelopeHeight * MinEnvelopeWidthToHeightRatio;
            bool hasLeftHook = trailingBounds.X - leadingBounds.X
                >= envelopeHeight * MinLeftLeadHeightRatio;
            bool hasVerticalBody = leadingBounds.Height
                >= Math.Max(trailingBounds.Height, 1.0) * MinEnvelopeToRadicandHeightRatio;

            if (startsAtOrAboveTop
                && startsAtOrLeftOfLeft
                && spansAcrossTheRest
                && hasEnvelopeAspect
                && hasLeftHook
                && hasVerticalBody)
            {
                radicalStrokes = leading;
                radicandStrokes = trailing;
                return true;
            }
        }

        return false;
    }
}
