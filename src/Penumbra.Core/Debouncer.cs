namespace Penumbra.Core;

/// <summary>
/// Quiet-period debouncer (Phase 4.5b): <see cref="Signal()"/> on every event; the callback fires once
/// after a full quiet period with no further signals. Built on <see cref="TimeProvider"/> so tests
/// drive it with fake time instead of sleeps. The callback runs on a timer thread — callers marshal
/// to their UI thread themselves. Thread-safe; a fire that loses the race to a concurrent
/// <see cref="Signal()"/>/<see cref="Cancel"/> is suppressed by generation check.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _quietPeriod;
    private readonly Action _fire;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private ITimer? _timer;
    private long _generation;
    private bool _disposed;

    public Debouncer(TimeSpan quietPeriod, Action fire, TimeProvider? time = null)
    {
        if (quietPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod), quietPeriod, "quiet period must be positive");
        }

        ArgumentNullException.ThrowIfNull(fire);
        _quietPeriod = quietPeriod;
        _fire = fire;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>An event happened: (re)start the default quiet period.</summary>
    public void Signal() => Signal(_quietPeriod);

    /// <summary>
    /// An event happened: (re)start the quiet period with a one-off duration. The override applies to
    /// this restart only — the next plain <see cref="Signal()"/> is back on the constructor default. Lets
    /// a caller stretch the quiet window after events that predict an immediate follow-up (an erase is
    /// usually followed by a rewrite) without two debouncers racing each other.
    /// </summary>
    public void Signal(TimeSpan quietPeriod)
    {
        if (quietPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod), quietPeriod, "quiet period must be positive");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer?.Dispose();
            long generation = ++_generation;
            _timer = _time.CreateTimer(_ => OnDue(generation), null, quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drop any pending fire without firing.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _generation++;   // invalidates a callback already past its due time but not yet run
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void OnDue(long generation)
    {
        lock (_gate)
        {
            // A Signal/Cancel that raced this callback owns the state now — stand down.
            if (_disposed || generation != _generation)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
        }

        // Outside the lock: the callback may Signal() again without deadlocking.
        _fire();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
