namespace NexNet.Inspector.App.Tests;

/// <summary>
/// A clock that moves only when told to, with timers that fire on <see cref="Advance"/>: drives debounces
/// (<c>Task.Delay(delay, timeProvider)</c>) without waiting in real time.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now;

    public ManualTime(DateTimeOffset? start = null) => _now = start ?? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>Timers not yet fired nor disposed.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate) return _timers.Count(t => t.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Timer timer = new(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on and fires every timer that came due, outside the lock, in due order.</summary>
    public void Advance(TimeSpan by)
    {
        List<Timer> due = [];
        lock (_gate)
        {
            _now += by;
            foreach (Timer timer in _timers)
            {
                if (timer.DueAt is { } at && at <= _now) due.Add(timer);
            }

            due.Sort(static (a, b) => a.DueAt!.Value.CompareTo(b.DueAt!.Value));
            foreach (Timer timer in due) timer.Rearm(_now);
        }

        foreach (Timer timer in due) timer.Fire();
    }

    private sealed class Timer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        /// <summary>Under the owner's lock: the next due time of a periodic timer, or none.</summary>
        public void Rearm(DateTimeOffset now) => DueAt = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? now + _period : null;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
