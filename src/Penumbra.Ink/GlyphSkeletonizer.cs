using SkiaSharp;

namespace Penumbra.Ink;

/// <summary>
/// Turns a filled glyph outline into single-line CENTERLINE polylines, so font glyphs draw as pen strokes
/// instead of hollow double-line contours. Pipeline: rasterize the filled <see cref="SKPath"/> to a binary
/// bitmap → Zhang-Suen thinning to a 1-px skeleton → walk the skeleton into polylines (endpoints first,
/// then leftover cycles like 'o') → prune spurs dangling off junctions → Douglas-Peucker simplify.
/// Fully deterministic: fixed scan orders, no randomness, no wall clock.
/// </summary>
internal static class GlyphSkeletonizer
{
    // Whitespace ring around the rasterized glyph so foreground never touches the bitmap border — the
    // thinning and neighbor walks can then skip bounds checks on the outermost ring.
    private const int Margin = 4;

    // Safety cap on the raster's longest side; extraction happens once per glyph then caches, and ≤256 px
    // keeps the whole pipeline in the low milliseconds.
    private const int MaxRasterSide = 240;

    // Spurs (thinning artifacts that dangle off a junction) shorter than this fraction of the raster's
    // longest side are dropped.
    private const double SpurFraction = 0.07;

    // Douglas-Peucker tolerance in raster pixels: strokes stop being one-sample-per-pixel but curves keep
    // their shape at glyph scale.
    private const float SimplifyTolerance = 1.5f;

    /// <summary>
    /// Extracts centerline polylines from the filled <paramref name="path"/>. Coordinates are raster pixels
    /// (uniformly scaled from path space, so aspect is preserved); callers are expected to re-normalize.
    /// Returns an empty list for degenerate paths that rasterize to nothing.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<SKPoint>> Skeletonize(SKPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        SKRect bounds = path.TightBounds;
        if (bounds.Width <= 0f && bounds.Height <= 0f)
        {
            return Array.Empty<IReadOnlyList<SKPoint>>();
        }

        // Uniform scale so the longest side fits the cap; never scale UP (small paths keep 1:1 pixels).
        float span = Math.Max(bounds.Width, bounds.Height);
        float scale = span > MaxRasterSide ? MaxRasterSide / span : 1f;

        int width = Math.Max(1, (int)MathF.Ceiling(bounds.Width * scale)) + 2 * Margin;
        int height = Math.Max(1, (int)MathF.Ceiling(bounds.Height * scale)) + 2 * Margin;

        bool[,] filled = Rasterize(path, bounds, scale, width, height);
        return SkeletonizeBitmap(filled);
    }

    /// <summary>
    /// The raster→strokes pipeline on an already-binary bitmap (<c>filled[x, y]</c>, true = ink). Exposed
    /// separately so it is unit-testable with synthetic shapes, no font required.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<SKPoint>> SkeletonizeBitmap(bool[,] filled)
    {
        ArgumentNullException.ThrowIfNull(filled);

        bool[,] skeleton = Thin(filled);
        List<List<(int X, int Y)>> polylines = Vectorize(skeleton);
        PruneSpurs(polylines, skeleton, filled.GetLength(0), filled.GetLength(1));

        var result = new List<IReadOnlyList<SKPoint>>(polylines.Count);
        foreach (List<(int X, int Y)> polyline in polylines)
        {
            result.Add(Simplify(polyline));
        }

        return result;
    }

    /// <summary>Fills the path in black on white with antialiasing off, then thresholds to a binary grid.</summary>
    private static bool[,] Rasterize(SKPath path, SKRect bounds, float scale, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(Margin - bounds.Left * scale, Margin - bounds.Top * scale);
            canvas.Scale(scale);
            canvas.DrawPath(path, paint);
        }

        var filled = new bool[width, height];
        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                filled[x, y] = pixels[y * rowBytes + x] < 128; // Gray8: 0 = black ink
            }
        }

        return filled;
    }

    /// <summary>
    /// Zhang-Suen thinning: two alternating sub-iterations peel boundary pixels that are safe to remove
    /// (2–6 neighbors, exactly one 0→1 transition around them, and the sub-iteration's directional guards)
    /// until nothing changes, leaving a 1-px-wide skeleton.
    /// </summary>
    private static bool[,] Thin(bool[,] filled)
    {
        int width = filled.GetLength(0);
        int height = filled.GetLength(1);
        var grid = (bool[,])filled.Clone();
        var toClear = new List<(int X, int Y)>();

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int step = 0; step < 2; step++)
            {
                toClear.Clear();
                for (int y = 1; y < height - 1; y++)
                {
                    for (int x = 1; x < width - 1; x++)
                    {
                        if (!grid[x, y])
                        {
                            continue;
                        }

                        // Neighbors p2..p9 clockwise from north, per the original paper's numbering.
                        bool p2 = grid[x, y - 1], p3 = grid[x + 1, y - 1], p4 = grid[x + 1, y];
                        bool p5 = grid[x + 1, y + 1], p6 = grid[x, y + 1], p7 = grid[x - 1, y + 1];
                        bool p8 = grid[x - 1, y], p9 = grid[x - 1, y - 1];

                        int neighbors = (p2 ? 1 : 0) + (p3 ? 1 : 0) + (p4 ? 1 : 0) + (p5 ? 1 : 0)
                                      + (p6 ? 1 : 0) + (p7 ? 1 : 0) + (p8 ? 1 : 0) + (p9 ? 1 : 0);
                        if (neighbors < 2 || neighbors > 6)
                        {
                            continue;
                        }

                        int transitions = ((!p2 && p3) ? 1 : 0) + ((!p3 && p4) ? 1 : 0)
                                        + ((!p4 && p5) ? 1 : 0) + ((!p5 && p6) ? 1 : 0)
                                        + ((!p6 && p7) ? 1 : 0) + ((!p7 && p8) ? 1 : 0)
                                        + ((!p8 && p9) ? 1 : 0) + ((!p9 && p2) ? 1 : 0);
                        if (transitions != 1)
                        {
                            continue;
                        }

                        bool guard = step == 0
                            ? (!p2 || !p4 || !p6) && (!p4 || !p6 || !p8)  // step 1: p2·p4·p6 = 0 and p4·p6·p8 = 0
                            : (!p2 || !p4 || !p8) && (!p2 || !p6 || !p8); // step 2: p2·p4·p8 = 0 and p2·p6·p8 = 0
                        if (guard)
                        {
                            toClear.Add((x, y));
                        }
                    }
                }

                foreach ((int x, int y) in toClear)
                {
                    grid[x, y] = false;
                }

                changed |= toClear.Count > 0;
            }
        }

        return grid;
    }

    // Neighbor offsets in a fixed, deterministic scan order: orthogonals first so walks prefer straight
    // continuations over diagonal shortcuts (fewer stranded pixels), then diagonals.
    private static readonly (int Dx, int Dy)[] NeighborOffsets =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0), (1, -1), (1, 1), (-1, 1), (-1, -1),
    };

    // The same 8 neighbors in CIRCULAR order (clockwise from north) — required by the crossing number.
    private static readonly (int Dx, int Dy)[] CircularOffsets =
    {
        (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1),
    };

    /// <summary>
    /// Rutovitz crossing number: 0→1 transitions walking the 8-neighborhood in circular order. Classifies
    /// skeleton pixels robustly where a raw neighbor count over-detects junctions on diagonal staircases
    /// (adjacent neighbors count once, not twice): endpoints = 1, chain pixels = 2, junctions ≥ 3.
    /// </summary>
    private static int CrossingNumber(bool[,] skeleton, int x, int y)
    {
        int transitions = 0;
        for (int i = 0; i < 8; i++)
        {
            (int dx1, int dy1) = CircularOffsets[i];
            (int dx2, int dy2) = CircularOffsets[(i + 1) % 8];
            if (!skeleton[x + dx1, y + dy1] && skeleton[x + dx2, y + dy2])
            {
                transitions++;
            }
        }

        return transitions;
    }

    /// <summary>
    /// Walks the 1-px skeleton into polylines. Pass 1 starts at endpoints (crossing number 1) and follows
    /// the chain until it dead-ends or reaches a junction (crossing number ≥ 3), which is appended so
    /// meeting strokes share the point. Pass 2 sweeps up what has no endpoint: pure cycles ('o', '0') and
    /// junction-to-junction spans, walking both directions from the seed and closing loops back onto their
    /// first point.
    /// </summary>
    private static List<List<(int X, int Y)>> Vectorize(bool[,] skeleton)
    {
        int width = skeleton.GetLength(0);
        int height = skeleton.GetLength(1);
        var degree = new int[width, height];
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                if (skeleton[x, y])
                {
                    degree[x, y] = CrossingNumber(skeleton, x, y);
                }
            }
        }

        var visited = new bool[width, height];
        var polylines = new List<List<(int X, int Y)>>();

        // Pass 1: open chains, seeded from endpoints in row-major order for determinism.
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                if (skeleton[x, y] && degree[x, y] == 1 && !visited[x, y])
                {
                    polylines.Add(Walk((x, y), skeleton, degree, visited));
                }
            }
        }

        // Pass 2: cycles and junction-to-junction spans (all interior pixels are degree 2 and unvisited).
        for (int y = 1; y < height - 1; y++)
        {
            for (int x = 1; x < width - 1; x++)
            {
                if (!skeleton[x, y] || visited[x, y] || degree[x, y] >= 3)
                {
                    continue;
                }

                List<(int X, int Y)> forward = Walk((x, y), skeleton, degree, visited);

                // The seed may sit mid-span: walk the untaken direction too and stitch it on, reversed.
                foreach ((int dx, int dy) in NeighborOffsets)
                {
                    (int X, int Y) n = (x + dx, y + dy);
                    if (skeleton[n.X, n.Y] && !visited[n.X, n.Y] && degree[n.X, n.Y] < 3
                        && (forward.Count < 2 || forward[1] != n))
                    {
                        List<(int X, int Y)> backward = Walk(n, skeleton, degree, visited);
                        backward.Reverse();
                        backward.AddRange(forward);
                        forward = backward;
                        break;
                    }
                }

                // A pure cycle ends 8-adjacent to where it began: close it explicitly.
                if (forward.Count > 3 && IsAdjacent(forward[0], forward[^1]))
                {
                    forward.Add(forward[0]);
                }

                polylines.Add(forward);
            }
        }

        return polylines;
    }

    /// <summary>Follows the skeleton from <paramref name="start"/>, marking non-junction pixels visited.</summary>
    private static List<(int X, int Y)> Walk(
        (int X, int Y) start, bool[,] skeleton, int[,] degree, bool[,] visited)
    {
        var polyline = new List<(int X, int Y)> { start };
        visited[start.X, start.Y] = true;
        (int X, int Y) current = start;
        (int X, int Y)? previous = null;

        while (true)
        {
            (int X, int Y)? next = null;
            bool terminal = false;
            foreach ((int dx, int dy) in NeighborOffsets)
            {
                (int X, int Y) n = (current.X + dx, current.Y + dy);
                if (!skeleton[n.X, n.Y] || n == previous)
                {
                    continue;
                }

                if (degree[n.X, n.Y] >= 3)
                {
                    // Junctions terminate the walk but are appended (and left unvisited) so every branch
                    // arriving here shares the meeting point.
                    if (next is null)
                    {
                        next = n;
                        terminal = true;
                    }

                    continue;
                }

                if (!visited[n.X, n.Y])
                {
                    next = n; // an unvisited chain pixel beats stopping at a junction
                    terminal = false;
                    break;
                }
            }

            if (next is null)
            {
                break;
            }

            polyline.Add(next.Value);
            if (terminal)
            {
                break;
            }

            visited[next.Value.X, next.Value.Y] = true;
            previous = current;
            current = next.Value;
        }

        return polyline;
    }

    /// <summary>
    /// Drops thinning artifacts: short polylines that dangle off a junction (one end degree ≥ 3, so they are
    /// branches the thinning invented, not real strokes). Real short marks like '.' survive because they
    /// touch no junction. Never prunes the last remaining polyline.
    /// </summary>
    private static void PruneSpurs(
        List<List<(int X, int Y)>> polylines, bool[,] skeleton, int width, int height)
    {
        double threshold = SpurFraction * Math.Max(width, height);
        for (int i = polylines.Count - 1; i >= 0 && polylines.Count > 1; i--)
        {
            List<(int X, int Y)> polyline = polylines[i];
            bool danglesOffJunction = CrossingNumber(skeleton, polyline[0].X, polyline[0].Y) >= 3
                                   || CrossingNumber(skeleton, polyline[^1].X, polyline[^1].Y) >= 3;
            if (danglesOffJunction && ArcLength(polyline) < threshold)
            {
                polylines.RemoveAt(i);
            }
        }
    }

    private static double ArcLength(List<(int X, int Y)> polyline)
    {
        double length = 0;
        for (int i = 1; i < polyline.Count; i++)
        {
            double dx = polyline[i].X - polyline[i - 1].X;
            double dy = polyline[i].Y - polyline[i - 1].Y;
            length += Math.Sqrt(dx * dx + dy * dy);
        }

        return length;
    }

    private static bool IsAdjacent((int X, int Y) a, (int X, int Y) b)
        => Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1;

    /// <summary>Douglas-Peucker simplification so strokes are not one sample per pixel.</summary>
    private static IReadOnlyList<SKPoint> Simplify(List<(int X, int Y)> polyline)
    {
        if (polyline.Count <= 2)
        {
            var direct = new SKPoint[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                direct[i] = new SKPoint(polyline[i].X, polyline[i].Y);
            }

            return direct;
        }

        var keep = new bool[polyline.Count];
        keep[0] = keep[^1] = true;
        SimplifySegment(polyline, 0, polyline.Count - 1, keep);

        var points = new List<SKPoint>();
        for (int i = 0; i < polyline.Count; i++)
        {
            if (keep[i])
            {
                points.Add(new SKPoint(polyline[i].X, polyline[i].Y));
            }
        }

        return points;
    }

    private static void SimplifySegment(List<(int X, int Y)> polyline, int first, int last, bool[] keep)
    {
        if (last - first < 2)
        {
            return;
        }

        double maxDistance = -1;
        int maxIndex = first;
        for (int i = first + 1; i < last; i++)
        {
            double d = PerpendicularDistance(polyline[i], polyline[first], polyline[last]);
            if (d > maxDistance)
            {
                maxDistance = d;
                maxIndex = i;
            }
        }

        if (maxDistance > SimplifyTolerance)
        {
            keep[maxIndex] = true;
            SimplifySegment(polyline, first, maxIndex, keep);
            SimplifySegment(polyline, maxIndex, last, keep);
        }
    }

    private static double PerpendicularDistance((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0)
        {
            // Degenerate anchor (closed loop with identical ends): fall back to point distance.
            double px = p.X - a.X, py = p.Y - a.Y;
            return Math.Sqrt(px * px + py * py);
        }

        double cross = Math.Abs(dy * p.X - dx * p.Y + (double)b.X * a.Y - (double)b.Y * a.X);
        return cross / Math.Sqrt(lengthSquared);
    }
}
