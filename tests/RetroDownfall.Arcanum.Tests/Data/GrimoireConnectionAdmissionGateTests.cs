using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class GrimoireConnectionAdmissionGateTests
{

    private static readonly TimeSpan OpeningTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void Ordinary_open_ticket_is_available_before_closing()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        Assert.Equal(gate.CurrentGeneration, ticket.Generation);

        Result opened = ticket.MarkOpened();

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

    }

    [Fact]
    public async Task Connection_close_advances_generation_and_refuses_new_open_before_native_io()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        long ordinaryGeneration = gate.CurrentGeneration;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(1));

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Assert.Equal(ordinaryGeneration + 1, lease.Generation);

        using SqliteConnection refused = new();

        _ = Assert.Throws<GrimoireMaintenanceUnavailableException>(
            () => gate.AcquireOrdinaryOpen(refused));

        Result keptClosed = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Close_waits_for_a_preexisting_native_open_attempt_to_resolve()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        TaskCompletionSource nativeOpenEntered = NewBarrier();

        TaskCompletionSource allowNativeFailure = NewBarrier();

        Task openAttempt = SimulateFailingNativeOpenAsync(
            ticket,
            nativeOpenEntered,
            allowNativeFailure);

        await nativeOpenEntered.Task;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(2));

        Task<Result<IGrimoireExclusiveClosedLease>> close = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        Assert.False(close.IsCompleted);

        allowNativeFailure.TrySetResult();

        await openAttempt;

        Result<IGrimoireExclusiveClosedLease> closed = await close;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result keptClosed = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Open_that_loses_the_generation_race_must_be_refused_after_physical_close()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        TaskCompletionSource nativeOpenEntered = NewBarrier();

        TaskCompletionSource allowNativeCompletion = NewBarrier();

        TaskCompletionSource refusalObserved = NewBarrier();

        TaskCompletionSource physicalCloseCompleted = NewBarrier();

        Task openAttempt = SimulateRacingNativeOpenAsync(
            ticket,
            nativeOpenEntered,
            allowNativeCompletion,
            refusalObserved,
            physicalCloseCompleted);

        await nativeOpenEntered.Task;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(3));

        Task<Result<IGrimoireExclusiveClosedLease>> close = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        allowNativeCompletion.TrySetResult();

        await refusalObserved.Task;

        Assert.False(close.IsCompleted);

        Exception? failedShortcut = Record.Exception(ticket.MarkFailed);

        physicalCloseCompleted.TrySetResult();

        Exception? openCompletion = await Record.ExceptionAsync(() => openAttempt);

        _ = Assert.IsType<InvalidOperationException>(failedShortcut);

        Assert.Null(openCompletion);

        Result<IGrimoireExclusiveClosedLease> closed = await close;

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result keptClosed = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Opening_timeout_leaves_admission_closed_and_does_not_issue_a_closed_lease()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(4));

        Task<Result<IGrimoireExclusiveClosedLease>> close = gate
            .CloseConnectionAdmissionAsync(closing, CancellationToken.None)
            .AsTask();

        await clock.WaitForScheduledTimerCountAsync(1);

        clock.Advance(OpeningTimeout);

        Result<IGrimoireExclusiveClosedLease> timedOut = await close;

        Assert.True(timedOut.IsFailure);

        using SqliteConnection refused = new();

        _ = Assert.Throws<GrimoireMaintenanceUnavailableException>(
            () => gate.AcquireOrdinaryOpen(refused));

        ticket.MarkFailed();

    }

    [Fact]
    public async Task Owner_generation_and_double_disposition_mismatches_are_rejected()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(5);

        await using IGrimoireClosingOwner closing = Begin(gate, owner);

        Result<IGrimoireClosingOwner> foreignOwner = gate.BeginOrResumeExclusive(Owner(6));

        Assert.True(foreignOwner.IsFailure);

        GrimoireConnectionAdmissionGate otherGate = CreateGate();

        Result<IGrimoireExclusiveClosedLease> foreignGeneration =
            await otherGate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(foreignGeneration.IsFailure);

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result<IGrimoireExclusiveClosedLease> duplicateClose =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(duplicateClose.IsFailure);

        Result first = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Result second = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.True(second.IsFailure);

    }

    [Fact]
    public async Task Next_open_generation_completes_once_only_after_commit_reopen()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        long observedGeneration = gate.CurrentGeneration;

        Task<long> nextOpen = gate.WaitForNextOpenGenerationAsync(
            observedGeneration,
            CancellationToken.None);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(7));

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        Assert.False(nextOpen.IsCompleted);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result reopened = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        Assert.Equal(observedGeneration + 1, await nextOpen);

        Result duplicate = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        Assert.True(duplicate.IsFailure);

        Assert.Equal(observedGeneration + 1, await nextOpen);

    }

    [Fact]
    public async Task Keep_closed_never_completes_the_next_open_generation()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        using CancellationTokenSource stopping = new();

        Task<long> nextOpen = gate.WaitForNextOpenGenerationAsync(
            gate.CurrentGeneration,
            stopping.Token);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(8));

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result keptClosed = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

        Assert.False(nextOpen.IsCompleted);

        stopping.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextOpen);

    }

    private static GrimoireConnectionAdmissionGate CreateGate(TimeProvider? timeProvider = null) =>
        new(timeProvider ?? TimeProvider.System, OpeningTimeout);

    private static IGrimoireClosingOwner Begin(
        GrimoireConnectionAdmissionGate gate,
        CovenantExclusiveRecoveryOwner owner)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    private static CovenantExclusiveRecoveryOwner Owner(byte seed) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{seed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(seed, 32).ToArray()));

    private static TaskCompletionSource NewBarrier() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task SimulateFailingNativeOpenAsync(
        IGrimoireConnectionOpenTicket ticket,
        TaskCompletionSource nativeOpenEntered,
        TaskCompletionSource allowNativeFailure)
    {

        nativeOpenEntered.TrySetResult();

        await allowNativeFailure.Task;

        ticket.MarkFailed();

    }

    private static async Task SimulateRacingNativeOpenAsync(
        IGrimoireConnectionOpenTicket ticket,
        TaskCompletionSource nativeOpenEntered,
        TaskCompletionSource allowNativeCompletion,
        TaskCompletionSource refusalObserved,
        TaskCompletionSource physicalCloseCompleted)
    {

        nativeOpenEntered.TrySetResult();

        await allowNativeCompletion.Task;

        Result admitted = ticket.MarkOpened();

        Assert.True(admitted.IsFailure);

        refusalObserved.TrySetResult();

        await physicalCloseCompleted.Task;

        ticket.MarkRefusedAfterOpen();

    }

    private sealed class ManualTimeProvider : TimeProvider
    {

        private readonly object _gate = new();

        private readonly List<ManualTimer> _timers = [];

        private readonly List<(int ExpectedCount, TaskCompletionSource Completion)> _timerWaiters = [];

        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private int _scheduledTimerCount;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {

            lock (_gate)
            {

                return _utcNow;

            }

        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {

            ArgumentNullException.ThrowIfNull(callback);

            ManualTimer timer = new(this, callback, state);

            _ = timer.Change(dueTime, period);

            return timer;

        }

        public void Advance(TimeSpan amount)
        {

            ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

            List<(TimerCallback Callback, object? State)> callbacks = [];

            lock (_gate)
            {

                _utcNow = _utcNow.Add(amount);

                foreach (ManualTimer timer in _timers.ToArray())
                {

                    timer.CollectDueCallbacks(_utcNow, callbacks);

                }

            }

            foreach ((TimerCallback callback, object? state) in callbacks)
            {

                callback(state);

            }

        }

        public Task WaitForScheduledTimerCountAsync(int expectedCount)
        {

            lock (_gate)
            {

                if (_scheduledTimerCount >= expectedCount)
                {

                    return Task.CompletedTask;

                }

                TaskCompletionSource waiter = NewBarrier();

                _timerWaiters.Add((expectedCount, waiter));

                return waiter.Task;

            }

        }

        private void ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {

            if (dueTime < Timeout.InfiniteTimeSpan)
            {

                throw new ArgumentOutOfRangeException(nameof(dueTime));

            }

            if (period < Timeout.InfiniteTimeSpan || period == TimeSpan.Zero)
            {

                throw new ArgumentOutOfRangeException(nameof(period));

            }

            List<TaskCompletionSource> completedWaiters = [];

            lock (_gate)
            {

                ObjectDisposedException.ThrowIf(timer.Disposed, timer);

                if (!_timers.Contains(timer))
                {

                    _timers.Add(timer);

                }

                timer.DueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : _utcNow.Add(dueTime);

                timer.Period = period;

                if (dueTime != Timeout.InfiniteTimeSpan)
                {

                    _scheduledTimerCount++;

                    for (int index = _timerWaiters.Count - 1; index >= 0; index--)
                    {

                        if (_timerWaiters[index].ExpectedCount > _scheduledTimerCount)
                        {

                            continue;

                        }

                        completedWaiters.Add(_timerWaiters[index].Completion);

                        _timerWaiters.RemoveAt(index);

                    }

                }

            }

            foreach (TaskCompletionSource waiter in completedWaiters)
            {

                waiter.TrySetResult();

            }

        }

        private void RemoveTimer(ManualTimer timer)
        {

            lock (_gate)
            {

                _ = _timers.Remove(timer);

            }

        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {

            public bool Disposed { get; private set; }

            public DateTimeOffset? DueAt { get; set; }

            public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {

                owner.ChangeTimer(this, dueTime, period);

                return true;

            }

            public void Dispose()
            {

                if (Disposed)
                {

                    return;

                }

                Disposed = true;

                owner.RemoveTimer(this);

            }

            public ValueTask DisposeAsync()
            {

                Dispose();

                return ValueTask.CompletedTask;

            }

            public void CollectDueCallbacks(
                DateTimeOffset now,
                List<(TimerCallback Callback, object? State)> callbacks)
            {

                if (Disposed || DueAt is not DateTimeOffset dueAt || dueAt > now)
                {

                    return;

                }

                callbacks.Add((callback, state));

                if (Period == Timeout.InfiniteTimeSpan)
                {

                    DueAt = null;

                }
                else
                {

                    do
                    {

                        dueAt = dueAt.Add(Period);

                    }
                    while (dueAt <= now);

                    DueAt = dueAt;

                }

            }

        }

    }

}
