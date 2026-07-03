using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Schedules an ordered list of timed strokes onto one master clock and answers "what ink is visible at
/// time t" — the pure, headless core of the timed-path render pipeline (Seam 4). Each stroke keeps its own
/// captured pace; consecutive strokes are separated by pen-up "air move" gaps during which nothing draws.
/// Non-monotonic sample timestamps are absorbed by advancing a cumulative maximum, so an out-of-order
/// sample shares the previous sample's scheduled instant rather than rewinding the clock.
/// </summary>
public sealed class StrokeTimeline
{
    private readonly IReadOnlyList<Stroke> _strokes;
    private readonly TimeSpan[] _starts;      // master-clock instant each stroke begins drawing
    private readonly TimeSpan[] _durations;   // each stroke's own draw duration (0 for point/empty strokes)
    private readonly TimeSpan[][] _monoTimes; // per-sample timestamps made non-decreasing (cumulative max)

    /// <summary>Builds a timeline from ordered strokes and the pen-up gaps between each consecutive pair.</summary>
    /// <param name="strokes">Strokes in the order they are drawn.</param>
    /// <param name="airMoves">
    /// Pen-up durations between consecutive strokes; must have exactly <c>strokes.Count - 1</c> entries
    /// (empty when there are fewer than two strokes). None may be negative.
    /// </param>
    public StrokeTimeline(IReadOnlyList<Stroke> strokes, IReadOnlyList<TimeSpan> airMoves)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        ArgumentNullException.ThrowIfNull(airMoves);

        int expectedGaps = Math.Max(0, strokes.Count - 1);
        if (airMoves.Count != expectedGaps)
        {
            throw new ArgumentException(
                $"Expected {expectedGaps} air-move gap(s) for {strokes.Count} stroke(s), got {airMoves.Count}.",
                nameof(airMoves));
        }

        _strokes = strokes;
        _starts = new TimeSpan[strokes.Count];
        _durations = new TimeSpan[strokes.Count];
        _monoTimes = new TimeSpan[strokes.Count][];

        TimeSpan cursor = TimeSpan.Zero;
        for (int i = 0; i < strokes.Count; i++)
        {
            if (i > 0)
            {
                TimeSpan gap = airMoves[i - 1];
                if (gap < TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException(nameof(airMoves), gap, "Air-move gaps must be non-negative.");
                }

                cursor += gap;
            }

            _monoTimes[i] = BuildMonotonicTimes(strokes[i].Samples);
            _durations[i] = StrokeDuration(_monoTimes[i]);
            _starts[i] = cursor;
            cursor += _durations[i];
        }

        TotalDuration = cursor;
    }

    /// <summary>Builds a timeline that inserts the same pen-up gap between every consecutive stroke.</summary>
    public StrokeTimeline(IReadOnlyList<Stroke> strokes, TimeSpan uniformAirMove = default)
        : this(strokes, UniformGaps(strokes, uniformAirMove))
    {
    }

    /// <summary>Sum of every stroke's own duration plus every air-move gap.</summary>
    public TimeSpan TotalDuration { get; }

    /// <summary>
    /// The drawable geometry at master-clock time <paramref name="t"/>: strokes finished by then whole (original
    /// Ids preserved), the one stroke in progress as a partial stroke ending in a single interpolated sample,
    /// and strokes not yet started omitted. <c>t</c> is clamped to <c>[0, TotalDuration]</c>. During an air-move
    /// gap only the finished strokes are returned.
    /// </summary>
    public IReadOnlyList<Stroke> SampleAt(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        else if (t > TotalDuration)
        {
            t = TotalDuration;
        }

        var visible = new List<Stroke>(_strokes.Count);
        for (int i = 0; i < _strokes.Count; i++)
        {
            TimeSpan start = _starts[i];
            TimeSpan end = start + _durations[i];

            // Finished (>= inclusive) is checked before in-progress so a zero-duration stroke, whose start and
            // end coincide, appears whole the instant its start is reached rather than as a degenerate partial.
            if (t >= end)
            {
                visible.Add(_strokes[i]);
            }
            else if (t > start)
            {
                visible.Add(PartialStroke(i, t - start));
            }
            // else: not started yet — omit (and every later stroke starts even later).
        }

        return visible;
    }

    /// <summary>The stroke up to <paramref name="elapsed"/> into its own draw, capped by one interpolated sample.</summary>
    private Stroke PartialStroke(int index, TimeSpan elapsed)
    {
        Stroke stroke = _strokes[index];
        IReadOnlyList<StrokeSample> samples = stroke.Samples;
        TimeSpan[] mono = _monoTimes[index];

        // Target instant within the stroke's own timeline. The in-progress branch guarantees 0 < elapsed <
        // duration, so target sits strictly between the first and last sample: a straddling pair always exists.
        TimeSpan target = mono[0] + elapsed;

        int last = 0; // last full sample at or before target
        while (last + 1 < mono.Length && mono[last + 1] <= target)
        {
            last++;
        }

        var partial = new List<StrokeSample>(last + 2);
        for (int i = 0; i <= last; i++)
        {
            partial.Add(samples[i]);
        }

        StrokeSample a = samples[last];
        StrokeSample b = samples[last + 1];
        TimeSpan span = mono[last + 1] - mono[last];
        // span is > 0 here: had it been 0, mono[last+1] <= target would hold and the scan would have advanced.
        double fraction = span > TimeSpan.Zero ? (double)(target - mono[last]).Ticks / span.Ticks : 0;
        partial.Add(StrokeMath.Lerp(a, b, fraction));

        // Same Id as the source stroke: a partial is the same ink still being drawn, not a new stroke.
        return stroke with { Samples = partial };
    }

    private static TimeSpan[] BuildMonotonicTimes(IReadOnlyList<StrokeSample> samples)
    {
        var mono = new TimeSpan[samples.Count];
        TimeSpan running = TimeSpan.Zero;
        for (int i = 0; i < samples.Count; i++)
        {
            TimeSpan time = samples[i].Time;
            running = i == 0 ? time : (time > running ? time : running);
            mono[i] = running;
        }

        return mono;
    }

    private static TimeSpan StrokeDuration(TimeSpan[] monoTimes) =>
        monoTimes.Length == 0 ? TimeSpan.Zero : monoTimes[^1] - monoTimes[0];

    private static IReadOnlyList<TimeSpan> UniformGaps(IReadOnlyList<Stroke> strokes, TimeSpan gap)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        int count = Math.Max(0, strokes.Count - 1);
        var gaps = new TimeSpan[count];
        Array.Fill(gaps, gap);
        return gaps;
    }
}
