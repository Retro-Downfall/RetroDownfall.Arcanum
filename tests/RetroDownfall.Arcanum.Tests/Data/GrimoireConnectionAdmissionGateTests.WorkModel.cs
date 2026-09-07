using System.Collections;
using System.Reflection;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Mixed_work_release_at_the_internal_zero_publication_window_cannot_be_missed(bool effect, int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable request = AcquireLifetime(gate, work: false);

        await using IGrimoireWorkLease work = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

        IAsyncDisposable terminal = work;

        if (effect)
        {

            Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));

            terminal = group!;

            await work.DisposeAsync();

        }

        await using IAsyncDisposable release = terminal;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(107));

        object closure = PrivateField(gate, "_closure")!;

        Task? zero = null;

        await RaceDedicated(
            () =>
            {

                lock (PrivateField(gate, "_sync")!)
                {

                    zero = Assert.IsAssignableFrom<Task>(GateMethod("PublishStageOneZeroWhileLocked").Invoke(gate, [closure]));

                }

            },
            () =>
            {

                RaceDedicated(
                    () => request.DisposeAsync().GetAwaiter().GetResult(),
                    () => release.DisposeAsync().GetAwaiter().GetResult(), order: 0).GetAwaiter().GetResult();

            }, order);

        await zero!.WaitAsync(BoundedWait);

        Assert.Equal(0, ReadCensus(gate, work: false));

        Assert.Equal(0, ReadCensus(gate, work: true));

        Assert.Empty(RequestMembers(gate));

        Assert.Empty(WorkMembers(gate));

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(101)]
    [InlineData(7919)]
    public async Task Bounded_work_epoch_model_preserves_exact_membership_effects_and_closure_isolation(int seed)
    {

        Random random = new(seed);

        for (int cycle = 0; cycle < 4; cycle++)
        {

            ManualTimeProvider clock = new();

            GrimoireConnectionAdmissionGate gate = CreateGate(clock);

            List<WorkModel> work = [];

            List<RequestModel> requests = [];

            List<CancellationTokenRegistration> registrations = [];

            IGrimoireClosingOwner? first = null;

            IGrimoireClosingOwner? second = null;

            try
            {

                for (int step = 0; step < 128; step++)
                {

                    int action = random.Next(7);

                    if (action == 0 || work.Count == 0)
                    {

                        AddModelWork(gate, work, registrations, generation: 1);

                    }
                    else if (action == 1 || requests.Count == 0)
                    {

                        AddModelRequest(gate, requests, generation: 1);

                    }
                    else if (action == 2)
                    {

                        ReleaseModelScope(work[random.Next(work.Count)]);

                    }
                    else if (action == 3)
                    {

                        WorkModel item = work[random.Next(work.Count)];

                        bool expected = item.ScopeLive && item.Group is null;

                        Assert.Equal(expected, item.Lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));

                        if (expected)
                        {

                            item.Group = group;

                        }
                        else
                        {

                            Assert.Null(group);

                        }

                    }
                    else if (action == 4)
                    {

                        ReleaseModelGroup(work[random.Next(work.Count)]);

                    }
                    else
                    {

                        ReleaseModelRequest(requests[random.Next(requests.Count)]);

                    }

                    AssertAdmissionModel(gate, work, requests);

                }

                WorkModel survivor = AddModelWork(gate, work, registrations, generation: 1);

                Assert.True(survivor.Lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? winning));

                survivor.Group = winning;

                RequestModel initiator = AddModelRequest(gate, requests, generation: 1);

                using SqliteConnection connection = new();

                first = Begin(gate, Owner(108), initiator.Lease, connection);

                initiator.Counted = false;

                MarkModelClosing(work);

                AssertAdmissionModel(gate, work, requests);

                Task<Result> timed = gate.DrainRequestAndWorkAsync(first, CancellationToken.None).AsTask();

                TaskCompletionSource oldSignal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

                using CancellationTokenSource cancelled = new();

                Task<Result> cancelledWait = gate.DrainRequestAndWorkAsync(first, cancelled.Token).AsTask();

                cancelled.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);

                Assert.False(oldSignal.Task.IsCompleted);

                await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

                clock.Advance(OpeningTimeout);

                Assert.Equal("Grimoire.WorkDrainTimeout", (await timed.WaitAsync(BoundedWait)).Error.Code);

                Task<Result> abandonedWait = gate.DrainRequestAndWorkAsync(first, CancellationToken.None).AsTask();

                Assert.True((await gate.AbortClosingAsync(first, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

                Assert.Equal(2, gate.CurrentGeneration);

                Assert.Null(PublishedZeroSignal(gate));

                foreach (WorkModel item in work)
                {

                    Assert.False(item.Lease.TryBeginExternalEffectGroup(out _));

                }

                AddModelWork(gate, work, registrations, generation: 2);

                AddModelRequest(gate, requests, generation: 2);

                AssertAdmissionModel(gate, work, requests);

                second = Begin(gate, Owner(109));

                MarkModelClosing(work);

                Task<Result> drain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

                TaskCompletionSource currentSignal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

                Assert.NotSame(oldSignal, currentSignal);

                oldSignal.TrySetResult();

                Assert.Equal("Grimoire.AdmissionLifecycleConflict", (await abandonedWait.WaitAsync(BoundedWait)).Error.Code);

                Assert.False(drain.IsCompleted);

                Assert.False(currentSignal.Task.IsCompleted);

                Assert.True((await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None)).IsFailure);

                List<Action> releases = [];

                foreach (RequestModel item in requests)
                {

                    releases.Add(() => ReleaseModelRequest(item));

                }

                foreach (WorkModel item in work)
                {

                    releases.Add(() => ReleaseModelScope(item));

                    releases.Add(() => ReleaseModelGroup(item));

                }

                Action[] shuffled = releases.ToArray();

                random.Shuffle(shuffled);

                foreach (Action release in shuffled)
                {

                    release();

                    release();

                    AssertAdmissionModel(gate, work, requests);

                }

                Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

                using CancellationTokenSource stopping = new();

                Task<long> reopened = gate.WaitForNextOpenGenerationAsync(2, stopping.Token);

                await using IGrimoireExclusiveClosedLease closed = (await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None)).Value;

                Assert.Equal(3, closed.Generation);

                bool keepClosed = (cycle & 1) == 0;

                Assert.True((await closed.CompleteAsync(keepClosed
                    ? CovenantExclusiveLeaseDisposition.KeepClosed
                    : CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

                if (keepClosed)
                {

                    Assert.False(reopened.IsCompleted);

                    Assert.False(gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out _));

                    stopping.Cancel();

                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopened);

                }
                else
                {

                    Assert.Equal(3, await reopened.WaitAsync(BoundedWait));

                    await using IGrimoireWorkLease current = (IGrimoireWorkLease)AcquireLifetime(gate, work: true);

                    Assert.Equal(3, current.Generation);

                    await current.DisposeAsync();

                    Assert.Empty(GateReachableOrdinaryLeases(gate));

                }

            }
            finally
            {

                foreach (WorkModel item in work)
                {

                    ReleaseModelScope(item);

                    ReleaseModelGroup(item);

                }

                foreach (RequestModel item in requests)
                {

                    ReleaseModelRequest(item);

                }

                foreach (CancellationTokenRegistration registration in registrations)
                {

                    registration.Dispose();

                }

                if (first is not null)
                {

                    await first.DisposeAsync();

                }

                if (second is not null)
                {

                    await second.DisposeAsync();

                }

            }

        }

    }

    private static WorkModel AddModelWork(GrimoireConnectionAdmissionGate gate, List<WorkModel> work,
        List<CancellationTokenRegistration> registrations, long generation)
    {

        Assert.True(gate.TryAcquireWorkLease((GrimoireWorkKind)((work.Count % 16) + 1), out IGrimoireWorkLease? lease));

        Assert.Equal(generation, lease!.Generation);

        WorkModel item = new(lease);

        work.Add(item);

        registrations.Add(lease.MaintenanceRevocation.Register(() => Interlocked.Increment(ref item.Callbacks)));

        return item;

    }

    private static RequestModel AddModelRequest(GrimoireConnectionAdmissionGate gate, List<RequestModel> requests, long generation)
    {

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? lease));

        Assert.Equal(generation, lease!.Generation);

        RequestModel item = new(lease);

        requests.Add(item);

        return item;

    }

    private static void ReleaseModelScope(WorkModel item)
    {

        item.Lease.DisposeAsync().GetAwaiter().GetResult();

        item.ScopeLive = false;

    }

    private static void ReleaseModelGroup(WorkModel item)
    {

        if (item.Group is not null)
        {

            item.Group.DisposeAsync().GetAwaiter().GetResult();

            item.Group.DisposeAsync().GetAwaiter().GetResult();

            item.Group = null;

            item.Cancelled |= item.RevocationPending;

        }

    }

    private static void ReleaseModelRequest(RequestModel item)
    {

        item.Lease.DisposeAsync().GetAwaiter().GetResult();

        item.Counted = false;

    }

    private static void MarkModelClosing(List<WorkModel> work)
    {

        foreach (WorkModel item in work.Where(static item => item.ScopeLive || item.Group is not null))
        {

            item.RevocationPending = true;

            item.Cancelled |= item.Group is null;

        }

    }

    private static void AssertAdmissionModel(GrimoireConnectionAdmissionGate gate, List<WorkModel> work, List<RequestModel> requests)
    {

        HashSet<object> expectedWork = work.Where(static item => item.ScopeLive || item.Group is not null).Select(static item => (object)item.Lease).ToHashSet();

        HashSet<object> expectedRequests = requests.Where(static item => item.Counted).Select(static item => (object)item.Lease).ToHashSet();

        Assert.Equal(expectedWork.Count, ReadCensus(gate, work: true));

        Assert.Equal(expectedRequests.Count, ReadCensus(gate, work: false));

        Assert.True(expectedWork.SetEquals(WorkMembers(gate)));

        Assert.True(expectedRequests.SetEquals(RequestMembers(gate)));

        foreach (WorkModel item in work)
        {

            Assert.Equal(item.Cancelled, item.Lease.MaintenanceRevocation.IsCancellationRequested);

            Assert.Equal(item.Cancelled ? 1 : 0, item.Callbacks);

            if (!item.ScopeLive && item.Group is null)
            {

                AssertUnlinkedWork(item.Lease);

            }

        }

    }

    private static HashSet<object> GateReachableOrdinaryLeases(GrimoireConnectionAdmissionGate gate)
    {

        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);

        HashSet<object> leases = new(ReferenceEqualityComparer.Instance);

        Stack<object> pending = new();

        pending.Push(gate);

        while (pending.TryPop(out object? value))
        {

            if (!seen.Add(value))
            {

                continue;

            }

            if (value is IGrimoireRequestLease or IGrimoireWorkLease)
            {

                leases.Add(value);

            }

            if (value is IEnumerable sequence)
            {

                foreach (object? item in sequence)
                {

                    if (item is not null)
                    {

                        pending.Push(item);

                    }

                }

            }

            if (ReferenceEquals(value, gate) || value.GetType().DeclaringType == typeof(GrimoireConnectionAdmissionGate))
            {

                foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {

                    if (!field.FieldType.IsValueType && field.GetValue(value) is object child)
                    {

                        pending.Push(child);

                    }

                }

            }

        }

        return leases;

    }

    private sealed class WorkModel(IGrimoireWorkLease lease)
    {

        internal IGrimoireWorkLease Lease { get; } = lease;

        internal bool ScopeLive { get; set; } = true;

        internal IGrimoireExternalEffectGroup? Group { get; set; }

        internal bool RevocationPending { get; set; }

        internal bool Cancelled { get; set; }

        internal int Callbacks;

    }

    private sealed class RequestModel(IGrimoireRequestLease lease)
    {

        internal IGrimoireRequestLease Lease { get; } = lease;

        internal bool Counted { get; set; } = true;

    }

}
