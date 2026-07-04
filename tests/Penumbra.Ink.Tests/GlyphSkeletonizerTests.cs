using Penumbra.Ink;
using SkiaSharp;

namespace Penumbra.Ink.Tests;

/// <summary>
/// Synthetic-bitmap tests for the raster skeletonizer — no font involved, so failures here point at the
/// thinning/vectorizing pipeline itself, not at Caveat's outlines.
/// </summary>
public sealed class GlyphSkeletonizerTests
{
    /// <summary>Blank grid with a margin already accounted for by the caller's shape coordinates.</summary>
    private static bool[,] Grid(int width, int height) => new bool[width, height];

    private static void FillRect(bool[,] grid, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                grid[x, y] = true;
            }
        }
    }

    private static double ArcLength(IReadOnlyList<SKPoint> polyline)
    {
        double length = 0;
        for (int i = 1; i < polyline.Count; i++)
        {
            length += SKPoint.Distance(polyline[i], polyline[i - 1]);
        }

        return length;
    }

    [Fact]
    public void HorizontalBarSkeletonizesToSingleCenterline()
    {
        // A filled 40×8 bar: the skeleton must be ONE stroke near the vertical center — not the two long
        // edges the old contour approach would have produced.
        bool[,] grid = Grid(52, 20);
        FillRect(grid, 6, 6, 40, 8);
        const float centerY = 6 + 8 / 2f; // = 10

        IReadOnlyList<IReadOnlyList<SKPoint>> polylines = GlyphSkeletonizer.SkeletonizeBitmap(grid);

        Assert.Single(polylines);
        IReadOnlyList<SKPoint> line = polylines[0];
        Assert.All(line, p => Assert.InRange(p.Y, centerY - 2f, centerY + 2f));

        // It must actually span the bar, not be a fragment.
        float spanX = line.Max(p => p.X) - line.Min(p => p.X);
        Assert.True(spanX > 30f, $"expected the centerline to span the bar, got x-span {spanX}");
    }

    [Fact]
    public void FilledRingSkeletonizesToOneClosedLoop_NotTwoConcentricContours()
    {
        // A donut (outer r=15, inner r=8): centerline is ONE loop near r≈11.5. Two loops at r≈8 and r≈15
        // would mean we reproduced the contour bug.
        const int cx = 24, cy = 24;
        bool[,] grid = Grid(48, 48);
        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 48; x++)
            {
                double r = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                grid[x, y] = r is >= 8 and <= 15;
            }
        }

        IReadOnlyList<IReadOnlyList<SKPoint>> polylines = GlyphSkeletonizer.SkeletonizeBitmap(grid);

        Assert.Single(polylines);
        IReadOnlyList<SKPoint> loop = polylines[0];

        // Every point sits in a band around the mid radius — a single centerline ring.
        Assert.All(loop, p =>
        {
            double r = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            Assert.InRange(r, 9.0, 14.0);
        });

        // Closed-ish: the loop returns to where it started.
        Assert.True(SKPoint.Distance(loop[0], loop[^1]) <= 2f, "expected the ring skeleton to close on itself");
    }

    [Fact]
    public void FilledLShapeYieldsOneOrTwoStrokesTracingTheL()
    {
        // An L: vertical 8×30 bar joined to a horizontal 30×8 bar. The skeleton should trace the L as one
        // continuous stroke or two meeting strokes — never the outline of the L's border.
        bool[,] grid = Grid(44, 44);
        FillRect(grid, 6, 6, 8, 30);  // vertical arm
        FillRect(grid, 6, 28, 30, 8); // horizontal arm (overlaps the vertical arm's foot)

        IReadOnlyList<IReadOnlyList<SKPoint>> polylines = GlyphSkeletonizer.SkeletonizeBitmap(grid);

        Assert.InRange(polylines.Count, 1, 2);

        // The combined ink must reach both arm tips (near the top of the vertical and the right of the
        // horizontal) and its total length must be near the sum of the two centerlines — roughly 30+30 px —
        // not the ~140 px perimeter a contour trace would give.
        var all = polylines.SelectMany(p => p).ToList();
        Assert.Contains(all, p => p.Y < 12f);
        Assert.Contains(all, p => p.X > 30f);

        double total = polylines.Sum(ArcLength);
        Assert.InRange(total, 35.0, 75.0);
    }

    [Fact]
    public void SameBitmapTwiceProducesIdenticalOutput()
    {
        bool[,] grid = Grid(44, 44);
        FillRect(grid, 6, 6, 8, 30);
        FillRect(grid, 6, 28, 30, 8);

        IReadOnlyList<IReadOnlyList<SKPoint>> first = GlyphSkeletonizer.SkeletonizeBitmap(grid);
        IReadOnlyList<IReadOnlyList<SKPoint>> second = GlyphSkeletonizer.SkeletonizeBitmap(grid);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Count, second[i].Count);
            for (int j = 0; j < first[i].Count; j++)
            {
                Assert.Equal(first[i][j], second[i][j]);
            }
        }
    }
}
