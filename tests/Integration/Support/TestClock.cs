namespace Reservation.Tests.Integration.Support;

/// <summary>
/// A settable <see cref="TimeProvider"/>. Its timers (used by <c>CancellationTokenSource</c> timeouts and
/// <c>PeriodicTimer</c>) fire only when the clock is moved with <see cref="Set"/> or <see cref="Advance"/>,
/// so polling and timeouts are deterministic in tests.
/// </summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public DateTimeOffset Now => GetUtcNow();

    /// <summary>Timers waiting for a due time: above zero means something (e.g. the dispatcher's poll) is waiting on this clock.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(t => t.DueAt is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Set(DateTimeOffset now)
    {
        lock (_gate)
        {
            _now = now;
        }

        FireDueTimers();
    }

    public DateTimeOffset Advance(TimeSpan by)
    {
        DateTimeOffset now;
        lock (_gate)
        {
            _now += by;
            now = _now;
        }

        FireDueTimers();
        return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    private void FireDueTimers()
    {
        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                due = _timers.Find(t => t.DueAt is { } at && at <= _now);
                if (due is null)
                {
                    return;
                }

                due.DueAt = due.Period > TimeSpan.Zero && due.Period != Timeout.InfiniteTimeSpan ? due.DueAt + due.Period : null;
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer(TestClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            }

            // A zero due time fires at once, but never inside CreateTimer's caller.
            if (dueTime == TimeSpan.Zero)
            {
                ThreadPool.QueueUserWorkItem(_ => clock.FireDueTimers());
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
