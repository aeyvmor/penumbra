using AngouriMath;
using Penumbra.Core;

namespace Penumbra.Graphing;

/// <summary>
/// The real <see cref="IDomainSampler"/>: compiles a <see cref="GraphCandidate"/>'s right-hand side into a
/// native <c>double -&gt; double</c> lambda (AngouriMath's <c>Entity.Compile</c>, ~15x faster than repeated
/// substitution) once per call, then evaluates it at each evenly spaced sample point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Gap discipline:</b> a non-finite (<c>NaN</c>/<c>±Infinity</c>) sample never becomes a fabricated point.
/// It ends the current <see cref="GraphSegment"/>; the next finite sample starts a new one. This is how an
/// asymptote (<c>1/x</c> at <c>x=0</c>) or a domain edge (<c>sqrt(x)</c>/<c>ln(x)</c> for <c>x&lt;=0</c>)
/// breaks the polyline honestly instead of drawing a false connecting line — verified against the real
/// AngouriMath 1.4.0 compiled delegate: <c>1/x</c> at <c>x=0</c> compiles to <c>+Infinity</c>,
/// <c>sqrt(x)</c> at <c>x&lt;0</c> to <c>NaN</c>, both caught by <see cref="double.IsFinite(double)"/>.
/// </para>
/// <para>
/// <b>Determinism:</b> the sample grid is exactly <c>x_i = Min + i*(Max-Min)/(N-1)</c> for <c>i</c> in
/// <c>[0, N-1]</c>, with the first and last points forced to the exact <see cref="GraphDomain.Min"/>/
/// <see cref="GraphDomain.Max"/> bounds (not the accumulated-step value) so endpoint rounding never drifts.
/// Recompiling the same <see cref="Entity"/> was verified to reproduce bit-identical results.
/// </para>
/// </remarks>
public sealed class DomainSampler : IDomainSampler
{
    /// <summary>The fewest points a caller may request — one segment needs at least two to draw a line.</summary>
    public const int MinSampleCount = 2;

    /// <summary>The most points a single call may request, bounding memory/CPU for one sampling call.</summary>
    public const int MaxSampleCount = 100_000;

    private readonly ILocalMetricsSink _metricsSink;
    private readonly TimeProvider _timeProvider;

    public DomainSampler()
        : this(NoOpLocalMetricsSink.Instance, TimeProvider.System)
    {
    }

    public DomainSampler(ILocalMetricsSink metricsSink, TimeProvider? timeProvider = null)
    {
        _metricsSink = metricsSink ?? throw new ArgumentNullException(nameof(metricsSink));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public GraphSamplingOutcome SampleSeries(GraphCandidate candidate, GraphDomain domain, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(domain);
        if (sampleCount < MinSampleCount || sampleCount > MaxSampleCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount), sampleCount,
                $"sample count must be between {MinSampleCount} and {MaxSampleCount}");
        }

        using MetricTimingScope timing = MetricTimingScope.Start(_metricsSink, MetricOperation.GraphSampling, _timeProvider);

        Func<double, double> compiled;
        try
        {
            compiled = candidate.Expression.Compile<double, double>(MathS.Var(candidate.IndependentVariable));
        }
        catch (Exception)
        {
            timing.Fail();
            return GraphSamplingOutcome.Refused(
                GraphSamplingRefusalReason.UncompilableExpression, "the expression could not be compiled");
        }

        try
        {
            GraphSeries series = SampleCore(compiled, domain, sampleCount);
            timing.Complete(series.Segments.Sum(segment => segment.Points.Count));
            return GraphSamplingOutcome.Sampled(series);
        }
        catch (Exception)
        {
            timing.Fail();
            throw;
        }
    }

    private static GraphSeries SampleCore(Func<double, double> compiled, GraphDomain domain, int sampleCount)
    {
        var segments = new List<GraphSegment>();
        List<GraphPoint>? current = null;
        var step = (domain.Max - domain.Min) / (sampleCount - 1);

        for (var i = 0; i < sampleCount; i++)
        {
            var x = i switch
            {
                0 => domain.Min,
                _ when i == sampleCount - 1 => domain.Max,
                _ => domain.Min + (step * i),
            };

            double y;
            try
            {
                y = compiled(x);
            }
            catch (Exception)
            {
                // A single pathological sample (e.g. an overflow at one x) is a gap, not a whole-series
                // failure — the failure classes here are "silent wrong" < "gap" per the project's ordering,
                // and a gap is the honest one.
                y = double.NaN;
            }

            if (double.IsFinite(x) && double.IsFinite(y))
            {
                current ??= new List<GraphPoint>();
                current.Add(new GraphPoint(x, y));
            }
            else if (current is not null)
            {
                segments.Add(new GraphSegment(current));
                current = null;
            }
        }

        if (current is not null)
        {
            segments.Add(new GraphSegment(current));
        }

        return new GraphSeries(segments);
    }
}
