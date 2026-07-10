using Penumbra.Core;

namespace Penumbra.Core.Tests;

/// <summary>
/// Phase 4.5b: the quiet-period contract, proven on fake time (no sleeps, no flakiness). The
/// debouncer is what turns "pen-lift" into "recognize a beat later, unless the pen came back down".
/// </summary>
public sealed class DebouncerTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);

    [Fact]
    public void Fires_Once_AfterQuietPeriod()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal();
        time.Advance(TimeSpan.FromMilliseconds(599));
        Assert.Equal(0, fired);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fired);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, fired);   // one signal, one fire — never a repeat
    }

    [Fact]
    public void ReSignal_RestartsTheQuietPeriod()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal();
        time.Advance(TimeSpan.FromMilliseconds(400));
        debouncer.Signal();   // pen touched down again — the beat starts over

        time.Advance(TimeSpan.FromMilliseconds(400));   // 800ms since first signal, 400 since second
        Assert.Equal(0, fired);

        time.Advance(TimeSpan.FromMilliseconds(200));   // 600 since the second signal
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Cancel_DropsThePendingFire()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal();
        debouncer.Cancel();
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void SignalAfterFire_ArmsAgain()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal();
        time.Advance(Quiet);
        debouncer.Signal();
        time.Advance(Quiet);

        Assert.Equal(2, fired);
    }

    [Fact]
    public void SignalFromInsideTheCallback_DoesNotDeadlock_AndReArms()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        Debouncer? debouncer = null;
        debouncer = new Debouncer(Quiet, () =>
        {
            fired++;
            if (fired == 1)
            {
                debouncer!.Signal();   // a fire may schedule follow-up work
            }
        }, time);

        debouncer.Signal();
        time.Advance(Quiet);
        Assert.Equal(1, fired);

        time.Advance(Quiet);
        Assert.Equal(2, fired);

        debouncer.Dispose();
    }

    [Fact]
    public void Dispose_SuppressesEverything()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal();
        debouncer.Dispose();
        time.Advance(TimeSpan.FromSeconds(5));
        debouncer.Signal();   // post-dispose signals are ignored, not an error
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void NonPositiveQuietPeriod_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Debouncer(TimeSpan.Zero, () => { }, new FakeTimeProvider()));
    }

    // ---- one-off quiet-period override (s19 erase grace) --------------------------------------------

    [Fact]
    public void SignalWithOverride_WaitsThatLong_NotTheDefault()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal(TimeSpan.FromMilliseconds(2200));
        time.Advance(TimeSpan.FromMilliseconds(2199));
        Assert.Equal(0, fired);   // the default 600ms did NOT apply

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void OverrideIsOneOff_NextPlainSignalUsesTheDefaultAgain()
    {
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal(TimeSpan.FromMilliseconds(2200));
        time.Advance(TimeSpan.FromMilliseconds(2200));
        Assert.Equal(1, fired);

        debouncer.Signal();
        time.Advance(Quiet);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void PlainSignal_SupersedesAPendingLongOverride()
    {
        // Erase (long grace) followed by a rewrite (plain signal): the rewrite's shorter beat owns the
        // timer — the user should not keep waiting out the erase grace after they already rewrote.
        var time = new FakeTimeProvider();
        int fired = 0;
        using var debouncer = new Debouncer(Quiet, () => fired++, time);

        debouncer.Signal(TimeSpan.FromMilliseconds(2200));
        time.Advance(TimeSpan.FromMilliseconds(300));
        debouncer.Signal();

        time.Advance(Quiet);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void SignalWithNonPositiveOverride_IsRejected()
    {
        using var debouncer = new Debouncer(Quiet, () => { }, new FakeTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => debouncer.Signal(TimeSpan.Zero));
    }

    /// <summary>
    /// Minimal controllable clock: timers fire in due order when <see cref="Advance"/> walks past
    /// them. Enough surface for the debouncer (one-shot timers, dispose); not a general fake.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = new();
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime,
            };
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            DateTimeOffset target = _now + by;
            while (true)
            {
                FakeTimer? next = _timers
                    .Where(t => !t.Disposed && t.Due is not null && t.Due <= target)
                    .OrderBy(t => t.Due)
                    .FirstOrDefault();
                if (next is null)
                {
                    break;
                }

                _now = next.Due!.Value;
                next.Due = null;   // one-shot: fired timers don't recur (debouncer never uses periods)
                next.Fire();
            }

            _now = target;
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public FakeTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            public DateTimeOffset? Due { get; set; }
            public bool Disposed { get; private set; }

            public void Fire() => _callback(_state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;   // debouncer recreates instead

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
