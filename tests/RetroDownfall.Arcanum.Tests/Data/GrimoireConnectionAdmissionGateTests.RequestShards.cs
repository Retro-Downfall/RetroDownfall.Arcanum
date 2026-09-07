using System.Reflection;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Fact]
    public async Task Request_membership_reclaims_exact_nodes_and_links_after_churn()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireConnectionAdmissionGate other = CreateGate();

        object[] shards = RequestShards(gate);

        object[] otherShards = RequestShards(other);

        Assert.NotEmpty(shards);

        Assert.Equal(0, shards.Length & (shards.Length - 1));

        Assert.Equal(shards.Length, shards.Distinct(ReferenceEqualityComparer.Instance).Count());

        Assert.DoesNotContain(shards, shard => otherShards.Contains(shard));

        await using IAsyncDisposable first = AcquireLifetime(gate, work: false);

        await using IAsyncDisposable second = AcquireLifetime(gate, work: false);

        for (int index = 0; index < 2000; index++)
        {

            IAsyncDisposable removed = AcquireLifetime(gate, work: false);

            await removed.DisposeAsync();

            Assert.Null(PrivateProperty(removed, "PreviousMember"));

            Assert.Null(PrivateProperty(removed, "NextMember"));

            Assert.False(Assert.IsType<bool>(PrivateProperty(removed, "IsLinked")));

        }

        Assert.Equal(2, ReadCensus(gate, work: false));

        Assert.Equal(new HashSet<object>([first, second]), RequestMembers(gate).ToHashSet());

        Assert.Empty(RequestMembers(other));

        await first.DisposeAsync();

        Assert.Same(second, Assert.Single(RequestMembers(gate)));

        await second.DisposeAsync();

        Assert.Empty(RequestMembers(gate));

        Assert.Equal(0, ReadCensus(gate, work: false));

        Assert.Equal(shards, RequestShards(gate));

    }

    [Fact]
    public async Task Request_admission_and_release_do_not_take_the_cold_monitor()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        object cold = PrivateField(gate, "_sync")!;

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(cold, held, release);

        Task? acquirer = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            acquirer = StartDedicated(() => AllocateAndReleaseLifetime(gate, work: false));

            await acquirer.WaitAsync(BoundedWait);

            Assert.Equal(0, ReadCensus(gate, work: false));

        }
        finally
        {

            release.Set();

            await holder.WaitAsync(BoundedWait);

            if (acquirer is not null)
            {

                await acquirer.WaitAsync(BoundedWait);

            }

        }

    }

    [Fact]
    public async Task Closing_publication_must_join_even_the_last_empty_request_shard_before_zero()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        object epoch = PrivateField(gate, "_epoch")!;

        object last = RequestShards(gate)[^1];

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(last, held, release);

        IGrimoireClosingOwner? owner = null;

        Task? begin = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            begin = StartDedicated(() => owner = Begin(gate, Owner(88)));

            await StartDedicated(() => Assert.True(SpinWait.SpinUntil(
                () => EpochPhase(epoch) == "Closing", BoundedWait))).WaitAsync(BoundedWait + BoundedWait);

            Assert.False(begin.IsCompleted);

            Assert.Equal(0, ReadCensus(gate, work: false));

            object closure = PrivateField(gate, "_closure")!;

            Assert.False(Assert.IsType<bool>(PrivateProperty(closure, "StageOneDrained")));

            Assert.Null(PublishedZeroSignal(gate));

        }
        finally
        {

            release.Set();

            await holder.WaitAsync(BoundedWait);

            if (begin is not null)
            {

                await begin.WaitAsync(BoundedWait);

            }

        }

        await using IGrimoireClosingOwner closing = owner!;

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        Assert.Null(PublishedZeroSignal(gate));

    }

    [Fact]
    public async Task Request_waiting_at_its_selected_shard_is_refused_after_closing_publication()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        object epoch = PrivateField(gate, "_epoch")!;

        TaskCompletionSource<object> selected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<Thread> attempting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ManualResetEventSlim attempt = new();

        IGrimoireRequestLease? output = null;

        bool admitted = true;

        Task acquirer = StartDedicated(() =>
        {

            IAsyncDisposable probe = AcquireLifetime(gate, work: false);

            object shard = PrivateProperty(probe, "Shard")!;

            probe.DisposeAsync().GetAwaiter().GetResult();

            selected.TrySetResult(shard);

            Assert.True(attempt.Wait(BoundedWait));

            attempting.TrySetResult(Thread.CurrentThread);

            admitted = gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out output);

        });

        object target = await selected.Task.WaitAsync(BoundedWait);

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(target, held, release);

        IGrimoireClosingOwner? owner = null;

        Task? begin = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            attempt.Set();

            Thread acquirerThread = await attempting.Task.WaitAsync(BoundedWait);

            await WaitForMonitorBlock(acquirerThread);

            begin = StartDedicated(() => owner = Begin(gate, Owner(89)));

            await StartDedicated(() => Assert.True(SpinWait.SpinUntil(
                () => EpochPhase(epoch) == "Closing", BoundedWait))).WaitAsync(BoundedWait + BoundedWait);

            Assert.False(begin.IsCompleted);

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

        Assert.Empty(RequestMembers(gate));

        Assert.Equal(0, ReadCensus(gate, work: false));

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Request_captured_before_abort_cannot_enter_the_fresh_epoch_after_its_shard_unblocks()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        await using IAsyncDisposable blocker = AcquireLifetime(gate, work: true);

        TaskCompletionSource<object> selected = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<Thread> attempting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using ManualResetEventSlim attempt = new();

        bool admitted = true;

        IGrimoireRequestLease? output = null;

        Task acquirer = StartDedicated(() =>
        {

            IAsyncDisposable probe = AcquireLifetime(gate, work: false);

            object shard = PrivateProperty(probe, "Shard")!;

            probe.DisposeAsync().GetAwaiter().GetResult();

            selected.TrySetResult(shard);

            Assert.True(attempt.Wait(BoundedWait));

            attempting.TrySetResult(Thread.CurrentThread);

            admitted = gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out output);

        });

        object target = await selected.Task.WaitAsync(BoundedWait);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(100));

        object oldEpoch = PrivateField(gate, "_epoch")!;

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(target, held, release);

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            attempt.Set();

            await WaitForMonitorBlock(await attempting.Task.WaitAsync(BoundedWait));

            await TimeoutAndAbort(gate, clock, closing);

            Assert.NotSame(oldEpoch, PrivateField(gate, "_epoch"));

            Assert.False(acquirer.IsCompleted);

        }
        finally
        {

            attempt.Set();

            release.Set();

            await holder.WaitAsync(BoundedWait);

            await acquirer.WaitAsync(BoundedWait);

        }

        Assert.False(admitted);

        Assert.Null(output);

        Assert.Empty(RequestMembers(gate));

        Assert.Equal(0, ReadCensus(gate, work: false));

        await blocker.DisposeAsync();

        await using IAsyncDisposable current = AcquireLifetime(gate, work: false);

        Assert.Equal(2, ((IGrimoireRequestLease)current).Generation);

    }

    [Fact]
    public async Task Retired_epoch_stays_closed_and_old_request_membership_survives_abort_and_reclose()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        object oldEpoch = PrivateField(gate, "_epoch")!;

        await using IAsyncDisposable old = AcquireLifetime(gate, work: false);

        await using IGrimoireClosingOwner first = Begin(gate, Owner(90));

        await TimeoutAndAbort(gate, clock, first);

        object newEpoch = PrivateField(gate, "_epoch")!;

        Assert.NotSame(oldEpoch, newEpoch);

        Assert.Equal("Closed", EpochPhase(oldEpoch));

        Assert.Equal("Ordinary", EpochPhase(newEpoch));

        Assert.Null(PublishedZeroSignal(gate));

        await using IAsyncDisposable current = AcquireLifetime(gate, work: false);

        Assert.Equal(2, ReadCensus(gate, work: false));

        Assert.Equal(2, RequestMembers(gate).Count);

        await using IGrimoireClosingOwner second = Begin(gate, Owner(91));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

        await old.DisposeAsync();

        await old.DisposeAsync();

        Assert.Same(current, Assert.Single(RequestMembers(gate)));

        Assert.False(drain.IsCompleted);

        await current.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        await using IGrimoireExclusiveClosedLease closed = (await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None)).Value;

        Assert.Equal("Closed", EpochPhase(newEpoch));

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

        object reopened = PrivateField(gate, "_epoch")!;

        Assert.NotSame(newEpoch, reopened);

        Assert.Equal("Ordinary", EpochPhase(reopened));

        Assert.Equal(3L, PrivateProperty(reopened, "Generation"));

        Assert.Empty(RequestMembers(gate));

    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Promotion_and_request_disposal_remove_one_exact_shard_member(int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease request = acquired!;

        await using IAsyncDisposable sibling = AcquireLifetime(gate, work: false);

        Assert.Equal(2, RequestMembers(gate).Count);

        using SqliteConnection connection = new();

        Result<IGrimoireClosingOwner>? result = null;

        await RaceDedicated(
            () => result = gate.BeginOrResumeExclusive(Owner(92), request, connection),
            () => request.DisposeAsync().GetAwaiter().GetResult(), order);

        Assert.NotNull(result);

        if (order != 0)
        {

            Assert.Equal(order == -1, result.IsSuccess);

        }

        await using IGrimoireClosingOwner closing = result.IsSuccess ? result.Value : Begin(gate, Owner(92));

        Assert.Equal(1, ReadCensus(gate, work: false));

        Assert.Same(sibling, Assert.Single(RequestMembers(gate)));

        Assert.Null(PrivateProperty(request, "PreviousMember"));

        Assert.Null(PrivateProperty(request, "NextMember"));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        await sibling.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

    }

    private static object[] RequestShards(GrimoireConnectionAdmissionGate gate) =>
        Assert.IsAssignableFrom<Array>(PrivateField(gate, "_shards")).Cast<object>().ToArray();

    private static List<object> RequestMembers(GrimoireConnectionAdmissionGate gate)
    {

        List<object> members = [];

        foreach (object shard in RequestShards(gate))
        {

            lock (shard)
            {

                object? previous = null;

                for (object? request = PrivateProperty(shard, "Requests"); request is not null; request = PrivateProperty(request, "NextMember"))
                {

                    Assert.DoesNotContain(request, members);

                    Assert.Same(previous, PrivateProperty(request, "PreviousMember"));

                    Assert.Same(shard, PrivateProperty(request, "Shard"));

                    Assert.True(Assert.IsType<bool>(PrivateProperty(request, "IsLinked")));

                    members.Add(request);

                    previous = request;

                }

            }

        }

        return members;

    }

    private static object? PrivateField(object instance, string name)
    {

        FieldInfo? field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return field.GetValue(instance);

    }

    private static object? PrivateProperty(object instance, string name)
    {

        PropertyInfo? property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(property);

        return property.GetValue(instance);

    }

    private static string EpochPhase(object epoch) => PrivateProperty(epoch, "Phase")!.ToString()!;

    private static Task WaitForMonitorBlock(Thread thread) => StartDedicated(() => Assert.True(SpinWait.SpinUntil(
        () => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, BoundedWait)));

    private static Task HoldMonitor(object monitor, TaskCompletionSource held, ManualResetEventSlim release) => StartDedicated(() =>
    {

        lock (monitor)
        {

            held.TrySetResult();

            Assert.True(release.Wait(BoundedWait + BoundedWait));

        }

    });

}
