using Penumbra.Core;

namespace Penumbra.Ink;

/// <summary>
/// Caches the smoothed form of each stroke by <see cref="Stroke.Id"/> so rendering can draw the smoothed
/// polyline without re-running the smoother on every frame. The document now stores the <em>raw</em>
/// stroke (so the recognizer and glyph bank see the pen data as captured); smoothing is a display-only
/// concern that happens here at render time.
/// <para>
/// Strokes are immutable records, so a cached result never goes stale for a given id — <see cref="EvictMissing"/>
/// exists purely to bound memory as strokes are removed (clear/undo). Not thread-safe: use from one
/// thread (the render thread).
/// </para>
/// </summary>
public sealed class SmoothedStrokeCache
{
    private readonly IStrokeSmoother _smoother;
    private readonly Dictionary<Guid, Stroke> _cache = new();

    /// <param name="smoother">
    /// The smoother to apply. Must be pure (same input → same output) for cached results to stay valid.
    /// </param>
    public SmoothedStrokeCache(IStrokeSmoother smoother)
    {
        ArgumentNullException.ThrowIfNull(smoother);
        _smoother = smoother;
    }

    /// <summary>Returns the smoothed form of <paramref name="stroke"/>, computing it once per id.</summary>
    public Stroke GetSmoothed(Stroke stroke)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (_cache.TryGetValue(stroke.Id, out Stroke? cached))
        {
            return cached;
        }

        Stroke smoothed = _smoother.Smooth(stroke);
        _cache[stroke.Id] = smoothed;
        return smoothed;
    }

    /// <summary>Drops cached entries whose id is not in <paramref name="liveIds"/> to bound memory.</summary>
    public void EvictMissing(IEnumerable<Guid> liveIds)
    {
        ArgumentNullException.ThrowIfNull(liveIds);
        ISet<Guid> live = liveIds as ISet<Guid> ?? new HashSet<Guid>(liveIds);

        // Materialize the doomed keys first: can't remove from the dictionary while enumerating it.
        List<Guid> stale = _cache.Keys.Where(id => !live.Contains(id)).ToList();
        foreach (Guid id in stale)
        {
            _cache.Remove(id);
        }
    }
}
