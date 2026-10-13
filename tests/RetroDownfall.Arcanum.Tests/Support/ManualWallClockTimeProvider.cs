namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A deterministic clock whose wall time and timers move only when a test advances it. Unlike the plain fake clock,
/// timers created through it (<c>Task.Delay(delay, clock, token)</c>, <c>PeriodicTimer</c>) fire during <see cref="Advance"/>,
/// in due order, with the clock reading each timer's own due instant while its callback runs. Unlike the timer-only manual
/// clock, <see cref="GetUtcNow"/> follows the same advance, so a duration measured against stored instants is exact.
/// </summary>
public sealed class ManualWallClockTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();

    private readonly List<WallTimer> _timers = [];

    private readonly List<(Func<int, bool> Predicate, TaskCompletionSource Signal)> _waiters = [];

    private TimeSpan _elapsed;

    public ManualWallClockTimeProvider()
        : this(DateTimeOffset.UtcNow)
    {
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _elapsed.Ticks;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return start + _elapsed;
        }
    }

    /// <summary>Timers that have been created, are armed and have not been disposed.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(static timer => timer.DueAt is not null);
            }
        }
    }

    /// <summary>How long from now until the earliest armed timer fires, or null when none is armed.</summary>
    public TimeSpan? NextDueIn
    {
        get
        {
            lock (_gate)
            {
                TimeSpan? earliest = _timers.Where(static timer => timer.DueAt is not null).Min(static timer => timer.DueAt);

                return earliest is { } due ? due - _elapsed : null;
            }
        }
    }

    /// <summary>Completes once the number of armed timers satisfies <paramref name="predicate"/>, deterministically and without polling.</summary>
    public Task WhenArmedTimersAsync(Func<int, bool> predicate, TimeSpan timeout)
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (predicate(CountArmed())) return Task.CompletedTask;

            _waiters.Add((predicate, signal));
        }

        return signal.Task.WaitAsync(timeout);
    }

    private int CountArmed() => _timers.Count(static timer => timer.DueAt is not null);

    private void NotifyWaiters()
    {
        List<TaskCompletionSource> ready = [];

        lock (_gate)
        {
            int armed = CountArmed();

            foreach ((Func<int, bool> predicate, TaskCompletionSource signal) in _waiters.ToArray())
            {
                if (!predicate(armed)) continue;

                _ = _waiters.Remove((predicate, signal));

                ready.Add(signal);
            }
        }

        foreach (TaskCompletionSource signal in ready) signal.TrySetResult();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        WallTimer timer = new(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);
        }

        _ = timer.Change(dueTime, period);

        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));

        TimeSpan target;

        lock (_gate)
        {
            target = _elapsed + delta;
        }

        while (true)
        {
            WallTimer? next;

            lock (_gate)
            {
                next = _timers.Where(candidate => candidate.DueAt is { } due && due <= target)
                    .OrderBy(static candidate => candidate.DueAt)
                    .FirstOrDefault();

                if (next is null)
                {
                    _elapsed = target;

                    return;
                }

                _elapsed = next.DueAt!.Value;

                next.Fired();
            }

            next.Run();
        }
    }

    private void Remove(WallTimer timer)
    {
        lock (_gate)
        {
            _ = _timers.Remove(timer);
        }

        NotifyWaiters();
    }

    private sealed class WallTimer(ManualWallClockTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _period;

        internal TimeSpan? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._elapsed + dueTime;

                _period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }

            owner.NotifyWaiters();

            return true;
        }

        internal void Fired()
        {
            DueAt = _period is { } period ? DueAt + period : null;
        }

        internal void Run() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
