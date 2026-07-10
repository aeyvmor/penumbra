using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

/// <summary>
/// Eraser geometry is proven headlessly. The canvas supplies world coordinates and tolerance; these
/// tests intentionally know nothing about Avalonia, pointer devices, or zoom transforms.
/// </summary>
public sealed class StrokeHitTesterTests
{
    [Fact]
    public void PointBetweenPolylineSamplesHitsSegment()
    {
        Stroke stroke = Line(Guid.NewGuid(), 0, 0, 100, 0);

        IReadOnlyList<Guid> hits = StrokeHitTester.HitTest(new[] { stroke }, 50, 3, tolerance: 3);

        Assert.Equal(new[] { stroke.Id }, hits);
    }

    [Fact]
    public void PointPastEndpointDoesNotHitSegmentExtension()
    {
        Stroke stroke = Line(Guid.NewGuid(), 0, 0, 10, 0);

        IReadOnlyList<Guid> hits = StrokeHitTester.HitTest(new[] { stroke }, 13, 0, tolerance: 2.99);

        Assert.Empty(hits);
    }

    [Fact]
    public void SingleSampleDotUsesSameToleranceRule()
    {
        var dot = new Stroke(
            Guid.NewGuid(),
            new[] { new StrokeSample(4, 7, TimeSpan.Zero, 0.5) });

        Assert.Equal(new[] { dot.Id }, StrokeHitTester.HitTest(new[] { dot }, 7, 11, tolerance: 5));
        Assert.Empty(StrokeHitTester.HitTest(new[] { dot }, 7, 11, tolerance: 4.99));
    }

    [Fact]
    public void ReturnsAllHitsInDocumentOrder()
    {
        Stroke miss = Line(Guid.NewGuid(), 0, 100, 10, 100);
        Stroke firstHit = Line(Guid.NewGuid(), 0, 0, 10, 0);
        Stroke secondHit = Line(Guid.NewGuid(), 5, -10, 5, 10);

        IReadOnlyList<Guid> hits = StrokeHitTester.HitTest(
            new[] { miss, firstHit, secondHit },
            5,
            0,
            tolerance: 0);

        Assert.Equal(new[] { firstHit.Id, secondHit.Id }, hits);
    }

    [Fact]
    public void DuplicateHitIdsAreReturnedOnceAtFirstMatchingOccurrence()
    {
        Guid duplicateId = Guid.NewGuid();
        Stroke firstMiss = Line(duplicateId, 0, 50, 10, 50);
        Stroke firstHit = Line(duplicateId, 0, 0, 10, 0);
        Stroke duplicateHit = Line(duplicateId, 5, -10, 5, 10);

        IReadOnlyList<Guid> hits = StrokeHitTester.HitTest(
            new[] { firstMiss, firstHit, duplicateHit },
            5,
            0,
            tolerance: 0);

        Assert.Equal(new[] { duplicateId }, hits);
    }

    [Fact]
    public void EmptyAndSamplelessStrokesDoNotHit()
    {
        Assert.Empty(StrokeHitTester.HitTest(Array.Empty<Stroke>(), 0, 0, tolerance: 10));

        var sampleless = new Stroke(Guid.NewGuid(), Array.Empty<StrokeSample>());
        Assert.Empty(StrokeHitTester.HitTest(new[] { sampleless }, 0, 0, tolerance: 10));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    public void InvalidToleranceIsRejected(double tolerance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StrokeHitTester.HitTest(Array.Empty<Stroke>(), 0, 0, tolerance));
    }

    private static Stroke Line(Guid id, double x0, double y0, double x1, double y1) => new(
        id,
        new[]
        {
            new StrokeSample(x0, y0, TimeSpan.Zero, 0.5),
            new StrokeSample(x1, y1, TimeSpan.Zero, 0.5),
        });
}
