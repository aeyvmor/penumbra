using Penumbra.Core;
using Penumbra.Ink;

namespace Penumbra.Ink.Tests;

public sealed class StrokeResamplerTests
{
    private static Stroke Line(int count, double step)
    {
        var samples = new StrokeSample[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = new StrokeSample(i * step, 0, TimeSpan.FromMilliseconds(i * 10), 0.5);
        }

        return new Stroke(Guid.NewGuid(), samples);
    }

    private static double Distance(StrokeSample a, StrokeSample b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [Fact]
    public void PreservesEndpoints()
    {
        Stroke source = Line(count: 10, step: 5);

        Stroke result = new StrokeResampler(spacing: 3).Resample(source);

        Assert.Equal(source.Samples[0], result.Samples[0]);
        Assert.Equal(source.Samples[^1], result.Samples[^1]);
    }

    [Fact]
    public void ProducesApproximatelyUniformSpacing()
    {
        Stroke source = Line(count: 50, step: 7);

        Stroke result = new StrokeResampler(spacing: 4).Resample(source);

        // Every gap except the trailing remainder to the true endpoint should be ~spacing.
        for (int i = 1; i < result.Samples.Count - 1; i++)
        {
            double gap = Distance(result.Samples[i - 1], result.Samples[i]);
            Assert.InRange(gap, 4 - 1e-6, 4 + 1e-6);
        }
    }

    [Fact]
    public void InterpolatesTimeMonotonically()
    {
        Stroke result = new StrokeResampler(spacing: 3).Resample(Line(count: 20, step: 5));

        for (int i = 1; i < result.Samples.Count; i++)
        {
            Assert.True(result.Samples[i].Time >= result.Samples[i - 1].Time);
        }
    }

    [Fact]
    public void ReturnsInputUnchangedWhenTooFewSamples()
    {
        var single = new Stroke(Guid.NewGuid(), new[] { new StrokeSample(1, 1, TimeSpan.Zero, 0.5) });

        Stroke result = new StrokeResampler().Resample(single);

        Assert.Same(single.Samples, result.Samples);
    }

    [Fact]
    public void RejectsNonPositiveSpacing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StrokeResampler(spacing: 0));
    }
}
