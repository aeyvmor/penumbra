namespace Penumbra.Graphing.Tests;

public sealed class DomainSamplerTests
{
    private static readonly GraphDetector Detector = new();
    private static readonly DomainSampler Sampler = new();

    private static GraphCandidate CandidateFor(string latex)
    {
        var outcome = Detector.Detect(latex);
        Assert.True(outcome.IsAccepted, $"expected '{latex}' to be a graph candidate, was {outcome.Reason}");
        return outcome.Candidate!;
    }

    // ---- exact spot-check fixtures ----------------------------------------------------------------------

    [Fact]
    public void SampleSeries_Linear_IsExact()
    {
        var candidate = CandidateFor("y=x");
        var domain = GraphDomain.Create(-5, 5);

        var outcome = Sampler.SampleSeries(candidate, domain, 11);

        Assert.True(outcome.IsSampled);
        var points = outcome.Series!.Segments.Single().Points;
        Assert.Equal(11, points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var expectedX = -5.0 + i;
            Assert.Equal(expectedX, points[i].X);
            Assert.Equal(expectedX, points[i].Y); // y = x, exact identity
        }
    }

    [Fact]
    public void SampleSeries_Square_IsExact()
    {
        var candidate = CandidateFor("y=x^2");
        var domain = GraphDomain.Create(-2, 2);

        var outcome = Sampler.SampleSeries(candidate, domain, 5);

        Assert.True(outcome.IsSampled);
        var points = outcome.Series!.Segments.Single().Points;
        Assert.Equal(new[] { -2.0, -1.0, 0.0, 1.0, 2.0 }, points.Select(p => p.X));
        Assert.Equal(new[] { 4.0, 1.0, 0.0, 1.0, 4.0 }, points.Select(p => p.Y));
    }

    [Fact]
    public void SampleSeries_Sin_MatchesMathSinExactly()
    {
        // AngouriMath's compiled delegate was verified (scratch probe against the real 1.4.0 package) to
        // reproduce Math.Sin bit-for-bit — so this fixture asserts exact equality, not a tolerance.
        var candidate = CandidateFor(@"y=\sin(x)");
        var domain = GraphDomain.Create(0, 2 * Math.PI);

        var outcome = Sampler.SampleSeries(candidate, domain, 13);

        Assert.True(outcome.IsSampled);
        var points = outcome.Series!.Segments.Single().Points;
        Assert.Equal(13, points.Count);
        foreach (var point in points)
        {
            Assert.Equal(Math.Sin(point.X), point.Y);
        }

        // And spot-check the well-known exact values explicitly.
        Assert.Equal(0.0, points[0].Y); // sin(0)
        var quarter = points.Single(p => Math.Abs(p.X - Math.PI / 2) < 1e-9);
        Assert.Equal(1.0, quarter.Y); // sin(pi/2)
    }

    // ---- gap discipline -----------------------------------------------------------------------------------

    [Fact]
    public void SampleSeries_Asymptote_SplitsIntoTwoSegments()
    {
        var candidate = CandidateFor("y=1/x");
        var domain = GraphDomain.Create(-2, 2);

        var outcome = Sampler.SampleSeries(candidate, domain, 5); // x = -2,-1,0,1,2 — 0 is the asymptote

        Assert.True(outcome.IsSampled);
        var segments = outcome.Series!.Segments;
        Assert.Equal(2, segments.Count);
        Assert.Equal(new[] { -2.0, -1.0 }, segments[0].Points.Select(p => p.X));
        Assert.Equal(new[] { 1.0, 2.0 }, segments[1].Points.Select(p => p.X));

        // No fabricated point ever appears at the asymptote itself.
        Assert.DoesNotContain(segments.SelectMany(s => s.Points), p => p.X == 0);
    }

    [Fact]
    public void SampleSeries_SquareRoot_DropsNegativeDomainAsGap()
    {
        var candidate = CandidateFor(@"y=\sqrt{x}");
        var domain = GraphDomain.Create(-2, 2);

        var outcome = Sampler.SampleSeries(candidate, domain, 5); // x = -2,-1,0,1,2

        Assert.True(outcome.IsSampled);
        var segments = outcome.Series!.Segments;
        var segment = Assert.Single(segments); // only the non-negative half survives as one run
        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, segment.Points.Select(p => p.X));
        Assert.Equal(new[] { 0.0, 1.0, Math.Sqrt(2) }, segment.Points.Select(p => p.Y));
    }

    [Fact]
    public void SampleSeries_AllNonFinite_ProducesNoSegments()
    {
        var candidate = CandidateFor(@"y=\sqrt{x}");
        var domain = GraphDomain.Create(-5, -1); // sqrt of an entirely negative domain

        var outcome = Sampler.SampleSeries(candidate, domain, 5);

        Assert.True(outcome.IsSampled);
        Assert.Empty(outcome.Series!.Segments);
    }

    // ---- determinism ----------------------------------------------------------------------------------------

    [Fact]
    public void SampleSeries_IsDeterministicAcrossCalls()
    {
        var candidate = CandidateFor(@"y=\sin(x)");
        var domain = GraphDomain.Create(-3, 7);

        var first = Sampler.SampleSeries(candidate, domain, 101);
        var second = Sampler.SampleSeries(candidate, domain, 101);

        Assert.True(first.IsSampled);
        Assert.True(second.IsSampled);
        Assert.Equal(first.Series, second.Series);
    }

    [Fact]
    public void SampleSeries_EndpointsAreExactDomainBounds()
    {
        var candidate = CandidateFor("y=x");
        var domain = GraphDomain.Create(0, 1);

        var outcome = Sampler.SampleSeries(candidate, domain, 7);

        var points = outcome.Series!.Segments.Single().Points;
        Assert.Equal(0.0, points[0].X);
        Assert.Equal(1.0, points[^1].X); // never drifted by accumulated step multiplication
    }

    // ---- validation -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.NegativeInfinity)]
    public void GraphDomain_Create_RejectsNonFiniteBounds(double min, double max) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDomain.Create(min, max));

    [Fact]
    public void GraphDomain_Create_RejectsInvertedRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDomain.Create(5, -5));

    [Fact]
    public void GraphDomain_Create_RejectsEqualBounds() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphDomain.Create(3, 3));

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void SampleSeries_RejectsOutOfBoundsSampleCount(int sampleCount)
    {
        var candidate = CandidateFor("y=x");
        var domain = GraphDomain.Create(0, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => Sampler.SampleSeries(candidate, domain, sampleCount));
    }

    [Fact]
    public void SampleSeries_ThrowsOnNullCandidate() =>
        Assert.Throws<ArgumentNullException>(() => Sampler.SampleSeries(null!, GraphDomain.Create(0, 1), 5));

    [Fact]
    public void SampleSeries_ThrowsOnNullDomain()
    {
        var candidate = CandidateFor("y=x");
        Assert.Throws<ArgumentNullException>(() => Sampler.SampleSeries(candidate, null!, 5));
    }
}
