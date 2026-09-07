using System.Reflection;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Work_membership_reclaims_exact_nodes_and_links_after_churn(int survivors)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireConnectionAdmissionGate other = CreateGate();

        object[] shards = RequestShards(gate);

        List<IAsyncDisposable> live = [];

        await using IAsyncDisposable otherWork = AcquireLifetime(other, work: true);

        try
        {

            for (int index = 0; index < survivors; index++)
            {

                live.Add(AcquireLifetime(gate, work: true));

            }

            for (int index = 0; index < 4000; index++)
            {

                IAsyncDisposable removed = AcquireLifetime(gate, work: true);

                await removed.DisposeAsync();

                AssertUnlinkedWork(removed);

            }

            Assert.Equal(survivors, ReadCensus(gate, work: true));

            Assert.True(live.Cast<object>().ToHashSet().SetEquals(WorkMembers(gate)));

            Assert.True(live.Cast<object>().ToHashSet().SetEquals(GateReachableOrdinaryLeases(gate)));

            Assert.Same(otherWork, Assert.Single(WorkMembers(other)));

            Assert.DoesNotContain(shards, shard => RequestShards(other).Contains(shard));

            Assert.DoesNotContain(typeof(GrimoireConnectionAdmissionGate).GetFields(BindingFlags.Instance | BindingFlags.NonPublic), field =>
                field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(HashSet<>)
                && typeof(IGrimoireWorkLease).IsAssignableFrom(field.FieldType.GetGenericArguments()[0]));

        }
        finally
        {

            foreach (IAsyncDisposable lease in live)
            {

                await lease.DisposeAsync();

            }

        }

        Assert.Empty(WorkMembers(gate));

        Assert.Empty(GateReachableOrdinaryLeases(gate));

        Assert.Equal(0, ReadCensus(gate, work: true));

        Assert.Equal(shards, RequestShards(gate));

    }

    [Fact]
    public async Task Work_overflow_contention_preserves_exact_output_ambient_and_shard_membership()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        IGrimoireWorkLease?[] outputs = new IGrimoireWorkLease?[2];

        Exception?[] errors = new Exception?[2];

        using Barrier start = new(2);

        using ManualResetEventSlim finish = new();

        TaskCompletionSource ready = NewBarrier();

        int completed = 0;

        CensusField(work: true).SetValue(gate, long.MaxValue - 1);

        Task[] participants = Enumerable.Range(0, 2).Select(index => StartDedicated(() =>
        {

            int depth = AmbientLifetimeDepth();

            Assert.True(start.SignalAndWait(BoundedWait));

            errors[index] = Record.Exception(() => gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out outputs[index]));

            Assert.Equal(depth + (errors[index] is null ? 1 : 0), AmbientLifetimeDepth());

            if (Interlocked.Increment(ref completed) == 2)
            {

                ready.TrySetResult();

            }

            Assert.True(finish.Wait(BoundedWait));

            outputs[index]?.DisposeAsync().GetAwaiter().GetResult();

        })).ToArray();

        try
        {

            await ready.Task.WaitAsync(BoundedWait);

            Assert.Single(errors, static error => error is OverflowException);

            IGrimoireWorkLease winner = Assert.Single(outputs, static output => output is not null)!;

            Assert.Same(winner, Assert.Single(WorkMembers(gate)));

            Assert.Equal(long.MaxValue, ReadCensus(gate, work: true));

            Assert.Equal(1, gate.CurrentGeneration);

        }
        finally
        {

            CensusField(work: true).SetValue(gate, 1L);

            finish.Set();

            await Task.WhenAll(participants).WaitAsync(BoundedWait);

        }

        Assert.Equal(0, ReadCensus(gate, work: true));

        Assert.Empty(WorkMembers(gate));

    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Work_hot_operations_do_not_take_the_cold_monitor(int operation)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IGrimoireWorkLease work = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

        IGrimoireExternalEffectGroup? group = null;

        if (operation == 3)
        {

            Assert.True(work.TryBeginExternalEffectGroup(out group));

            await work.DisposeAsync();

        }

        TaskCompletionSource held = NewBarrier();

        using ManualResetEventSlim release = new();

        Task holder = HoldMonitor(PrivateField(gate, "_sync")!, held, release);

        Task? worker = null;

        try
        {

            await held.Task.WaitAsync(BoundedWait);

            worker = StartDedicated(() =>
            {

                switch (operation)
                {

                    case 0:
                        AllocateAndReleaseLifetime(gate, work: true);

                        break;

                    case 1:
                        work.DisposeAsync().GetAwaiter().GetResult();

                        break;

                    case 2:
                        Assert.True(work.TryBeginExternalEffectGroup(out group));

                        break;

                    case 3:
                        group!.DisposeAsync().GetAwaiter().GetResult();

                        break;

                }

            });

            await worker.WaitAsync(BoundedWait);

        }
        finally
        {

            release.Set();

            await holder.WaitAsync(BoundedWait);

            if (worker is not null)
            {

                await worker.WaitAsync(BoundedWait);

            }

            if (group is not null)
            {

                await group.DisposeAsync();

            }

        }

        await work.DisposeAsync();

        Assert.Equal(0, ReadCensus(gate, work: true));

    }

    [Theory]
    [MemberData(nameof(HostedWorkKinds))]
    public async Task Work_captured_shard_serializes_both_frontier_winners_for_every_kind(object value)
    {

        foreach (bool effectFirst in new[] { true, false })
        {

            GrimoireConnectionAdmissionGate gate = CreateGate();

            Assert.True(gate.TryAcquireWorkLease(Assert.IsType<GrimoireWorkKind>(value), out IGrimoireWorkLease? acquired));

            await using IGrimoireWorkLease work = acquired!;

            object shard = PrivateProperty(work, "Shard")!;

            object epoch = PrivateProperty(work, "Epoch")!;

            TaskCompletionSource held = NewBarrier();

            using ManualResetEventSlim release = new();

            IGrimoireExternalEffectGroup? group = null;

            bool admitted = false;

            Task holder = StartDedicated(() =>
            {

                lock (shard)
                {

                    if (effectFirst)
                    {

                        admitted = work.TryBeginExternalEffectGroup(out group);

                        Assert.True(admitted);

                    }

                    held.TrySetResult();

                    Assert.True(release.Wait(BoundedWait + BoundedWait));

                }

            });

            Task? begin = null;

            Task? effect = null;

            IGrimoireClosingOwner? owner = null;

            try
            {

                await held.Task.WaitAsync(BoundedWait);

                begin = StartDedicated(() => owner = Begin(gate, Owner(102)));

                await WaitForClosingEpoch(epoch);

                Assert.False(begin.IsCompleted);

                Assert.False(work.MaintenanceRevocation.IsCancellationRequested);

                Assert.False(Assert.IsType<bool>(PrivateProperty(PrivateField(gate, "_closure")!, "StageOneDrained")));

                Assert.Null(PublishedZeroSignal(gate));

                if (!effectFirst)
                {

                    TaskCompletionSource<Thread> attempting = new(TaskCreationOptions.RunContinuationsAsynchronously);

                    effect = StartDedicated(() =>
                    {

                        attempting.TrySetResult(Thread.CurrentThread);

                        admitted = work.TryBeginExternalEffectGroup(out group);

                    });

                    await WaitForMonitorBlock(await attempting.Task.WaitAsync(BoundedWait));

                    Assert.False(effect.IsCompleted);

                }

            }
            finally
            {

                release.Set();

                await holder.WaitAsync(BoundedWait);

                if (begin is not null)
                {

                    await begin.WaitAsync(BoundedWait);

                }

                if (effect is not null)
                {

                    await effect.WaitAsync(BoundedWait);

                }

            }

            await using IGrimoireClosingOwner closing = owner!;

            Assert.Equal(effectFirst, admitted);

            Assert.Equal(!effectFirst, work.MaintenanceRevocation.IsCancellationRequested);

            Assert.False(work.TryBeginExternalEffectGroup(out _));

            Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            await work.DisposeAsync();

            if (effectFirst)
            {

                Assert.Same(work, Assert.Single(WorkMembers(gate)));

                Assert.False(drain.IsCompleted);

                await group!.DisposeAsync();

            }
            else
            {

                Assert.Null(group);

            }

            Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

            Assert.True(work.MaintenanceRevocation.IsCancellationRequested);

            AssertUnlinkedWork(work);

        }

    }

    private static Task WaitForClosingEpoch(object epoch) => StartDedicated(() => Assert.True(SpinWait.SpinUntil(
        () => EpochPhase(epoch) == "Closing", BoundedWait)));

    private static void AssertUnlinkedWork(object work)
    {

        Assert.Null(PrivateProperty(work, "PreviousMember"));

        Assert.Null(PrivateProperty(work, "NextMember"));

        Assert.False(Assert.IsType<bool>(PrivateProperty(work, "IsLinked")));

    }

    private static List<object> WorkMembers(GrimoireConnectionAdmissionGate gate)
    {

        List<object> members = [];

        foreach (object shard in RequestShards(gate))
        {

            lock (shard)
            {

                object? previous = null;

                for (object? work = PrivateProperty(shard, "Work"); work is not null; work = PrivateProperty(work, "NextMember"))
                {

                    Assert.DoesNotContain(work, members);

                    Assert.Same(previous, PrivateProperty(work, "PreviousMember"));

                    Assert.Same(shard, PrivateProperty(work, "Shard"));

                    Assert.True(Assert.IsType<bool>(PrivateProperty(work, "IsLinked")));

                    members.Add(work);

                    previous = work;

                }

            }

        }

        return members;

    }

}
