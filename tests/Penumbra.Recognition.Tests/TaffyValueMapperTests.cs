using System.Globalization;
using Penumbra.Recognition;

namespace Penumbra.Recognition.Tests;

/// <summary>
/// The taffy drag→value arithmetic: whole-step snapping (round toward zero), last-written-place
/// stepping for decimals, and invariant-culture normalized output.
/// </summary>
public sealed class TaffyValueMapperTests
{
    [Theory]
    // no travel → identity
    [InlineData("5", 0.0, "5")]
    // one full step (default 14 px) in either direction
    [InlineData("5", 14.0, "6")]
    [InlineData("5", -14.0, "4")]
    // n steps
    [InlineData("5", 42.0, "8")]
    // sub-step travel rounds toward zero → identity (a value change requires a full step)
    [InlineData("5", 13.9, "5")]
    [InlineData("5", -13.9, "5")]
    // decimals step in the last written place and keep exactly k fractional digits
    [InlineData("2.50", 14.0, "2.51")]
    [InlineData("2.50", -14.0, "2.49")]
    // zero never carries a sign
    [InlineData("0.1", -14.0, "0.0")]
    // negative crossing — the mapper emits negatives; parenthesizing is Splice's job
    [InlineData("1", -28.0, "-1")]
    // large drags accumulate whole steps
    [InlineData("5", 1400.0, "105")]
    // normalization: leading zeros do not survive
    [InlineData("007", 14.0, "8")]
    public void MapsDragToSnappedValue(string original, double dx, string expected)
    {
        Assert.Equal(expected, TaffyValueMapper.Map(original, dx));
    }

    [Fact]
    public void CustomStepDistanceChangesTheSnap()
    {
        Assert.Equal("7", TaffyValueMapper.Map("5", 20.0, pixelsPerStep: 10.0));
    }

    [Fact]
    public void FormatsWithInvariantCultureRegardlessOfThreadCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // a decimal-comma culture
            Assert.Equal("2.51", TaffyValueMapper.Map("2.50", 14.0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
