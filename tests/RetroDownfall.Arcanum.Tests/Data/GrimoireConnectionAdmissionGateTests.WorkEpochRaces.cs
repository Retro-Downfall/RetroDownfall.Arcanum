using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Work_scope_and_successor_group_disposal_remove_only_the_exact_member(int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IGrimoireWorkLease work = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

        await using IGrimoireWorkLease sibling = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? previous));

        await previous!.DisposeAsync();

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? acquired));

        await using IGrimoireExternalEffectGroup group = acquired!;

        await previous.DisposeAsync();

        Assert.Same(group, PrivateProperty(work, "ActiveEffectGroup"));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(110));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await RaceDedicated(
            () => work.DisposeAsync().GetAwaiter().GetResult(),
            () => group.DisposeAsync().GetAwaiter().GetResult(), order);

        await work.DisposeAsync();

        await group.DisposeAsync();

        await previous.DisposeAsync();

        Assert.Null(PrivateProperty(work, "ActiveEffectGroup"));

        AssertUnlinkedWork(work);

        Assert.Same(sibling, Assert.Single(WorkMembers(gate)));

        Assert.Equal(1, ReadCensus(gate, work: true));

        Assert.False(drain.IsCompleted);

        await sibling.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        Assert.Empty(WorkMembers(gate));

        Assert.Equal(0, ReadCensus(gate, work: true));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Work_admission_captured_at_a_blocked_shard_cannot_cross_close_or_abort(bool abort)
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        await using IAsyncDisposable blocker = AcquireLifetime(gate, work: false);

        TaskCompletionSource<object> selected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<Thread> attempting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ManualResetEventSlim attempt = new();

        IGrimoireWorkLease? output = null;

        bool admitted = true;

        Task acquirer = StartDedicated(() =>
        {

            try
            {

                IAsyncDisposable probe = AcquireLifetime(gate, work: true);

                object shard;

                try
                {

                    shard = PrivateProperty(probe, "Shard")!;

                }
                finally
                {

                    probe.DisposeAsync().GetAwaiter().GetResult();

                }

                selected.TrySetResult(shard);

                Assert.True(attempt.Wait(BoundedWait));

                attempting.TrySetResult(Thread.CurrentThread);

                admitted = gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out output);

            }
            catch (Exception failure)
            {

                selected.TrySetException(failure);

                throw;

            }

        });

        object target;

        try
        {

            target = await selected.Task.WaitAsync(BoundedWait);

        }
        catch
        {

            attempt.Set();

            await acquirer;

            throw;

        }

        IGrimoireClosingOwner? owner = abort ? Begin(gate, Owner(103)) : null;

        object oldEpoch = PrivateField(gate, "_epoch")!;

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(target, held, release);

        Task? begin = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            attempt.Set();

            await WaitForMonitorBlock(await attempting.Task.WaitAsync(BoundedWait));

            if (abort)
            {

                await TimeoutAndAbort(gate, clock, owner!);

                Assert.Equal("Closed", EpochPhase(oldEpoch));

            }
            else
            {

                begin = StartDedicated(() => owner = Begin(gate, Owner(103)));

                await WaitForClosingEpoch(oldEpoch);

                Assert.False(begin.IsCompleted);

            }

            Assert.False(acquirer.IsCompleted);

        }
        finally
        {

            attempt.Set();

            release.Set();

            await holder.WaitAsync(BoundedWait);

            await acquirer.WaitAsync(BoundedWait);

            if (begin is not null)
            {

                await begin.WaitAsync(BoundedWait);

            }

        }

        await using IGrimoireClosingOwner closing = owner!;

        Assert.False(admitted);

        Assert.Null(output);

        Assert.Empty(WorkMembers(gate));

        Assert.Equal(0, ReadCensus(gate, work: true));

        await blocker.DisposeAsync();

        if (abort)
        {

            await using IGrimoireWorkLease current = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

            Assert.Equal(2, current.Generation);

        }
        else
        {

            Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        }

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Work_revocation_callback_reenters_cold_and_captured_shard_after_exact_accounting(bool effectWinner, bool abort)
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        await using IGrimoireWorkLease work = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

        object shard = PrivateProperty(work, "Shard")!;

        object cold = PrivateField(gate, "_sync")!;

        IGrimoireExternalEffectGroup? group = null;

        if (effectWinner)
        {

            Assert.True(work.TryBeginExternalEffectGroup(out group));

        }

        TaskCompletionSource ready = NewBarrier();

        using ManualResetEventSlim callback = new();

        using ManualResetEventSlim reentered = new();

        int callbacks = 0;

        long expectedGeneration = abort ? 2 : 1;

        Task reader = StartDedicated(() =>
        {

            ready.TrySetResult();

            Assert.True(callback.Wait(BoundedWait));

            Assert.Equal(expectedGeneration, gate.CurrentGeneration);

            lock (shard)
            {

                Assert.False(work.TryBeginExternalEffectGroup(out _));

                reentered.Set();

            }

        });

        bool outsideLocks = false;

        bool reentryCompleted = false;

        bool terminalBeforeCallback = false;

        TaskCompletionSource? signal = null;

        using CancellationTokenRegistration registration = work.MaintenanceRevocation.Register(() =>
        {

            Interlocked.Increment(ref callbacks);

            outsideLocks = !Monitor.IsEntered(cold) && !Monitor.IsEntered(shard);

            terminalBeforeCallback = !effectWinner || (ReadCensus(gate, work: true) == 0 && signal!.Task.IsCompletedSuccessfully);

            callback.Set();

            reentryCompleted = reentered.Wait(BoundedWait);

            throw new InvalidOperationException("Expected callback failure.");

        });

        IGrimoireClosingOwner? first = null;

        IGrimoireClosingOwner? second = null;

        try
        {

            await ready.Task.WaitAsync(BoundedWait);

            await StartDedicated(() => first = Begin(gate, Owner(104))).WaitAsync(BoundedWait + BoundedWait);

            if (abort)
            {

                await TimeoutAndAbort(gate, clock, first!);

                await using IGrimoireWorkLease current = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

                Assert.Equal(2, WorkMembers(gate).Count);

                await current.DisposeAsync();

                second = Begin(gate, Owner(105));

            }

            Task<Result> drain = gate.DrainRequestAndWorkAsync(second ?? first!, CancellationToken.None).AsTask();

            signal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

            if (effectWinner)
            {

                Assert.Equal(0, callbacks);

                Assert.True(Assert.IsType<bool>(PrivateProperty(work, "RevocationPending")));

            }

            await work.DisposeAsync();

            if (group is not null)
            {

                Assert.False(drain.IsCompleted);

                await StartDedicated(() => group.DisposeAsync().GetAwaiter().GetResult()).WaitAsync(BoundedWait + BoundedWait);

                await group.DisposeAsync();

            }

            Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

            Assert.Equal(1, callbacks);

            Assert.True(outsideLocks);

            Assert.True(reentryCompleted);

            Assert.True(terminalBeforeCallback);

            AssertUnlinkedWork(work);

        }
        finally
        {

            callback.Set();

            await reader.WaitAsync(BoundedWait);

            if (group is not null)
            {

                await group.DisposeAsync();

            }

            if (second is not null)
            {

                await second.DisposeAsync();

            }

            if (first is not null)
            {

                await first.DisposeAsync();

            }

        }

    }

    [Fact]
    public async Task Work_scope_release_publishes_shared_ambient_death_before_waiting_to_unlink()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable work = AcquireLifetime(gate, work: true);

        object lifetime = PrivateProperty(work, "Lifetime")!;

        object shard = PrivateProperty(work, "Shard")!;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(106));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(shard, held, release);

        Task? disposer = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            disposer = StartDedicated(() => work.DisposeAsync().GetAwaiter().GetResult());

            await StartDedicated(() => Assert.True(SpinWait.SpinUntil(
                () => Assert.IsType<bool>(PrivateProperty(lifetime, "IsReleased")), BoundedWait))).WaitAsync(BoundedWait);

            Assert.False(disposer.IsCompleted);

            Assert.Equal(1, ReadCensus(gate, work: true));

            AssertFinisherRefused(gate);

            Assert.False(drain.IsCompleted);

        }
        finally
        {

            release.Set();

            await holder.WaitAsync(BoundedWait);

            if (disposer is not null)
            {

                await disposer.WaitAsync(BoundedWait);

            }

        }

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

    }

}
