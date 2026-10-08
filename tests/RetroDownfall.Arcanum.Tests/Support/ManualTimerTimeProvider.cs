namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when a test advances it, so a deadline test
/// decides exactly when the deadline lapses instead of racing a wall clock. A scheduler stall can
/// neither fire a deadline early nor hold one back.
/// </summary>
/// <remarks>
/// Callbacks run on the thread that calls <see cref="Advance"/>, after the clock's lock is released.
/// <see cref="TimersCreated"/> and <see cref="ActiveTimers"/> let a test assert that a call armed no
/// timer at all, or that none is left armed once the thing it bounded has finished.
/// </remarks>
public sealed class ManualTimerTimeProvider : TimeProvider
{
    private readonly object _gate = new();

    private readonly List<ManualTimer> _timers = [];

    private readonly TaskCompletionSource _firstTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TimeSpan _now;

    /// <summary>Completes when the first timer is created.</summary>
    public Task FirstTimerCreated => _firstTimer.Task;

    public int TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>The timers that are neither disposed nor spent.</summary>
    public int ActiveTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(static timer => timer.IsArmed);
            }
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);

            timer.Schedule(_now, dueTime, period);
        }

        _firstTimer.TrySetResult();

        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that has become due, in due order.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);

        List<ManualTimer> due = [];

        lock (_gate)
        {
            _now += by;

            due.AddRange(
                _timers
                    .Where(timer => timer.IsDue(_now))
                    .OrderBy(static timer => timer.DueAt));

            foreach (ManualTimer timer in due)
            {
                timer.MarkFired(_now);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(
        ManualTimerTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        private bool _disposed;

        private TimeSpan? _dueAt;

        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool IsArmed => !_disposed && _dueAt is not null;

        public TimeSpan DueAt => _dueAt ?? TimeSpan.MaxValue;

        public bool IsDue(TimeSpan now) => IsArmed && _dueAt <= now;

        public void Schedule(TimeSpan now, TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;

            _period = period;
        }

        public void MarkFired(TimeSpan now)
        {
            _dueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero
                ? null
                : now + _period;
        }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                Schedule(owner._now, dueTime, period);

                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;

                _dueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
