using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class StrokeWidthModelTests
{
    private static readonly StrokeWidthModel Model = new(minWidth: 1, maxWidth: 5, velocityHalfWidth: 1.5);

    [Fact]
    public void FullPressureGivesMinWidth()
    {
        Assert.Equal(1, Model.FromPressure(1), precision: 6);
    }

    [Fact]
    public void NoPressureGivesMaxWidth()
    {
        Assert.Equal(5, Model.FromPressure(0), precision: 6);
    }

    [Fact]
    public void PressureClampsOutOfRangeInput()
    {
        Assert.Equal(5, Model.FromPressure(-2), precision: 6);
        Assert.Equal(1, Model.FromPressure(3), precision: 6);
    }

    [Fact]
    public void RestingPenIsThickestByVelocity()
    {
        Assert.Equal(5, Model.FromVelocity(0), precision: 6);
    }

    [Fact]
    public void FasterMovementIsThinner()
    {
        double slow = Model.FromVelocity(0.5);
        double fast = Model.FromVelocity(5);

        Assert.True(fast < slow);
        Assert.InRange(fast, 1, 5);
    }

    [Fact]
    public void ComputeWidthsUsesPressureWhenAsked()
    {
        var stroke = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(0, 0, TimeSpan.FromMilliseconds(0), 1.0),
            new StrokeSample(1, 0, TimeSpan.FromMilliseconds(10), 0.0),
        });

        IReadOnlyList<double> widths = Model.ComputeWidths(stroke, usePressure: true);

        Assert.Equal(1, widths[0], precision: 6); // full pressure -> thin
        Assert.Equal(5, widths[1], precision: 6); // no pressure -> thick
    }

    [Fact]
    public void ComputeWidthsFallsBackToVelocity()
    {
        // Same constant pressure (mouse-like); the second sample moves much faster than the third.
        var fastThenSlow = new Stroke(Guid.NewGuid(), new[]
        {
            new StrokeSample(0, 0, TimeSpan.FromMilliseconds(0), 0.5),
            new StrokeSample(30, 0, TimeSpan.FromMilliseconds(10), 0.5), // 3 units/ms
            new StrokeSample(31, 0, TimeSpan.FromMilliseconds(20), 0.5), // 0.1 units/ms
        });

        IReadOnlyList<double> widths = Model.ComputeWidths(fastThenSlow, usePressure: false);

        Assert.True(widths[1] < widths[2]); // fast segment thinner than the slow one
    }

    [Fact]
    public void ComputeWidthsHandlesEmptyStroke()
    {
        var empty = new Stroke(Guid.NewGuid(), Array.Empty<StrokeSample>());

        Assert.Empty(Model.ComputeWidths(empty, usePressure: false));
    }

    [Theory]
    [InlineData(0, 4, 1.5)]
    [InlineData(2, 1, 1.5)]
    [InlineData(1, 4, 0)]
    public void RejectsInvalidConstruction(double min, double max, double half)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeWidthModel(min, max, half));
    }
}
