using Penumbra.Core;
using Penumbra.Ink;
using Penumbra.Recognition;

namespace Penumbra.Runtime;

public enum PageStampDecision
{
    Append,
    Replace,
    Refuse,
}

public enum PageStampRefusal
{
    None,
    MissingSource,
    InvalidGeometry,
    UnsafeHorizontalDrop,
}

/// <summary>The complete observable result of one answer-to-ink transaction.</summary>
public sealed record PageStampResult(
    PageStampDecision Decision,
    double? AppliedScale,
    IReadOnlyList<Stroke> SourceStrokes,
    IReadOnlyList<Guid> RemovedStrokeIds,
    IReadOnlyList<Stroke> AddedStrokes,
    bool HideSourceAnswer,
    PageStampRefusal Refusal);

/// <summary>
/// Applies the product stamp grammar without UI dependencies: match a target line by Y, replace a literal
/// on direct hit, otherwise append only when the drop is spatially safe. Successful transforms always use
/// fresh IDs and enter <see cref="InkDocument"/> as one synthesized-ink edit.
/// </summary>
public static class PageStampTransaction
{
    public static PageStampResult Apply(
        InkDocument document,
        IReadOnlyList<RegionRecognition> acceptedRegions,
        Guid sourceOwnerId,
        IReadOnlyList<Stroke>? sourceStrokes,
        double dx,
        double dy,
        double dropX,
        double dropY)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(acceptedRegions);
        if (sourceStrokes is null || sourceStrokes.Count == 0)
        {
            return Refused(PageStampRefusal.MissingSource);
        }
        if (!double.IsFinite(dx) || !double.IsFinite(dy)
            || !double.IsFinite(dropX) || !double.IsFinite(dropY)
            || !TryStrokeBounds(sourceStrokes, out double minX, out double minY, out double maxX, out double maxY))
        {
            return Refused(PageStampRefusal.InvalidGeometry);
        }

        double centreX = (minX + maxX) / 2;
        double centreY = (minY + maxY) / 2;
        RegionRecognition? targetRegion = DropTargetRegion(acceptedRegions, dropY);
        LiteralDropTarget? targetLiteral = LiteralTargetAt(
            document,
            acceptedRegions,
            dropX,
            dropY);

        double scale = 1.0;
        double sourceHeight = maxY - minY;
        if (sourceHeight > 0 && targetRegion is not null)
        {
            scale = PageAnswerMaterializer.ClampedMedianTokenHeight(targetRegion.Result.Tokens)
                / sourceHeight;
        }
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return Refused(PageStampRefusal.InvalidGeometry);
        }

        IReadOnlyList<Stroke> stamped = StrokeTransformer.Transform(
            sourceStrokes,
            dx,
            dy,
            scale,
            centreX,
            centreY);
        if (targetRegion is not null
            && targetLiteral is null
            && !IsNearLine(stamped, targetRegion))
        {
            return Refused(PageStampRefusal.UnsafeHorizontalDrop);
        }

        bool hideSourceAnswer = targetRegion?.Region.Id == sourceOwnerId;
        if (targetLiteral is not null)
        {
            Guid[] removed = targetLiteral.Run.SourceStrokeIds.ToArray();
            document.ReplaceStrokes(removed, stamped, StrokeOriginKind.SynthesizedInk);
            return new PageStampResult(
                PageStampDecision.Replace,
                scale,
                sourceStrokes.ToArray(),
                removed,
                stamped,
                hideSourceAnswer,
                PageStampRefusal.None);
        }

        document.AddStrokes(stamped, StrokeOriginKind.SynthesizedInk);
        return new PageStampResult(
            PageStampDecision.Append,
            scale,
            sourceStrokes.ToArray(),
            Array.Empty<Guid>(),
            stamped,
            hideSourceAnswer,
            PageStampRefusal.None);
    }

    private static PageStampResult Refused(PageStampRefusal reason) => new(
        PageStampDecision.Refuse,
        null,
        Array.Empty<Stroke>(),
        Array.Empty<Guid>(),
        Array.Empty<Stroke>(),
        HideSourceAnswer: false,
        reason);

    private static RegionRecognition? DropTargetRegion(
        IReadOnlyList<RegionRecognition> acceptedRegions,
        double dropY)
    {
        RegionRecognition? best = null;
        double bestDistance = double.MaxValue;
        foreach (RegionRecognition region in acceptedRegions)
        {
            InkBounds bounds = region.Region.Bounds;
            double pad = bounds.Height * 0.5;
            if (dropY < bounds.Y - pad || dropY > bounds.Y + bounds.Height + pad)
            {
                continue;
            }

            double distance = Math.Abs(dropY - (bounds.Y + bounds.Height / 2));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = region;
            }
        }
        return best;
    }

    private static LiteralDropTarget? LiteralTargetAt(
        InkDocument document,
        IReadOnlyList<RegionRecognition> acceptedRegions,
        double dropX,
        double dropY)
    {
        LiteralDropTarget? best = null;
        double bestDistance = double.MaxValue;
        HashSet<Guid> present = document.Strokes.Select(stroke => stroke.Id).ToHashSet();
        foreach (RegionRecognition region in acceptedRegions)
        {
            foreach (LiteralRun run in LiteralRuns.Find(region.Result.Tokens))
            {
                InkBounds bounds = run.UnionBounds;
                double pad = Math.Max(8, bounds.Height * 0.35);
                bool inside = dropX >= bounds.X - pad
                    && dropX <= bounds.X + bounds.Width + pad
                    && dropY >= bounds.Y - pad
                    && dropY <= bounds.Y + bounds.Height + pad;
                if (!inside
                    || run.SourceStrokeIds.Count == 0
                    || run.SourceStrokeIds.Any(id => !present.Contains(id)))
                {
                    continue;
                }

                double dx = dropX - (bounds.X + bounds.Width / 2);
                double dy = dropY - (bounds.Y + bounds.Height / 2);
                double distance = dx * dx + dy * dy;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new LiteralDropTarget(run);
                }
            }
        }
        return best;
    }

    private static bool IsNearLine(IReadOnlyList<Stroke> stamped, RegionRecognition target)
    {
        if (!TryStrokeBounds(stamped, out double minX, out _, out double maxX, out _))
        {
            return false;
        }

        InkBounds bounds = target.Region.Bounds;
        double gap = maxX < bounds.X
            ? bounds.X - maxX
            : minX > bounds.X + bounds.Width
                ? minX - (bounds.X + bounds.Width)
                : 0;
        double lineHeight = PageAnswerMaterializer.ClampedMedianTokenHeight(target.Result.Tokens);
        return gap <= Math.Max(24, lineHeight * 0.75);
    }

    private static bool TryStrokeBounds(
        IReadOnlyList<Stroke> strokes,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;
        bool any = false;
        foreach (Stroke stroke in strokes)
        {
            foreach (StrokeSample sample in stroke.Samples)
            {
                if (!double.IsFinite(sample.X) || !double.IsFinite(sample.Y))
                {
                    return false;
                }
                any = true;
                minX = Math.Min(minX, sample.X);
                minY = Math.Min(minY, sample.Y);
                maxX = Math.Max(maxX, sample.X);
                maxY = Math.Max(maxY, sample.Y);
            }
        }
        return any;
    }

    private sealed record LiteralDropTarget(LiteralRun Run);
}
