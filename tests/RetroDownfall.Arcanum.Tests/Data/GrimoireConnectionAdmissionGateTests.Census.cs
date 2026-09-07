using System.Reflection;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_lifetime_has_no_eager_terminal_waiter(bool work)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable lease = AcquireLifetime(gate, work);

        Assert.DoesNotContain(OwnedLifetimeObjects(lease), static value =>
            value is Task or TaskCompletionSource);

        Assert.Equal(0, gate.MaterializedTerminalCallbacks);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_lifetime_allocation_excludes_terminal_waiter(bool work)
    {

        await StartDedicated(() =>
        {

            GrimoireConnectionAdmissionGate gate = CreateGate();

            for (int index = 0; index < 100; index++)
            {

                AllocateAndReleaseLifetime(gate, work);

            }

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int index = 0; index < 1000; index++)
            {

                AllocateAndReleaseLifetime(gate, work);

            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.InRange(allocated, 0, 280_000);

        }).WaitAsync(BoundedWait);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Census_exhaustion_does_not_publish_partial_admission(bool work)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable sibling = AcquireLifetime(gate, work);

        FieldInfo count = CensusField(work);

        count.SetValue(gate, long.MaxValue);

        int depth = AmbientLifetimeDepth();

        IGrimoireRequestLease? requestOutput = sibling as IGrimoireRequestLease;

        IGrimoireWorkLease? workOutput = sibling as IGrimoireWorkLease;

        try
        {

            Assert.Throws<OverflowException>(() =>
            {

                if (work)
                {

                    gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out workOutput);

                }
                else
                {

                    gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out requestOutput);

                }

            });

            Assert.Same(sibling, work ? workOutput : requestOutput);

            Assert.Equal(long.MaxValue, ReadCensus(gate, work));

            Assert.Equal(depth, AmbientLifetimeDepth());

            Assert.Equal(1, gate.CurrentGeneration);

        }
        finally
        {

            count.SetValue(gate, 1L);

        }

        await sibling.DisposeAsync();

        Assert.Equal(0, ReadCensus(gate, work));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(85));

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Mixed_census_tracks_exact_promotion_scope_and_effect_obligations_per_gate()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireConnectionAdmissionGate other = CreateGate();

        await using IAsyncDisposable otherRequest = AcquireLifetime(other, work: false);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? request));

        await using IGrimoireRequestLease promoted = request!;

        await using IAsyncDisposable sibling = AcquireLifetime(gate, work: false);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect));

        await using IGrimoireExternalEffectGroup group = effect!;

        Assert.Equal(2, ReadCensus(gate, work: false));

        Assert.Equal(1, ReadCensus(gate, work: true));

        using SqliteConnection exact = new();

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(86), promoted, exact);

        Assert.Equal(1, ReadCensus(gate, work: false));

        await promoted.DisposeAsync();

        await promoted.DisposeAsync();

        Assert.Equal(1, ReadCensus(gate, work: false));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await work.DisposeAsync();

        Assert.Equal(1, ReadCensus(gate, work: true));

        await sibling.DisposeAsync();

        Assert.Equal(0, ReadCensus(gate, work: false));

        Assert.False(drain.IsCompleted);

        await group.DisposeAsync();

        await group.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        Assert.Equal(0, ReadCensus(gate, work: true));

        Assert.Equal(1, ReadCensus(other, work: false));

        Assert.Equal(0, ReadCensus(other, work: true));

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contending_admissions_reserve_the_last_count_without_wrapping(bool work)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        FieldInfo count = CensusField(work);

        count.SetValue(gate, long.MaxValue - 1);

        IAsyncDisposable?[] leases = new IAsyncDisposable?[2];

        Exception?[] failures = new Exception?[2];

        void Acquire(int index)
        {

            failures[index] = Record.Exception(() => leases[index] = AcquireLifetime(gate, work));

        }

        try
        {

            await RaceDedicated(() => Acquire(0), () => Acquire(1), order: 0);

            Assert.Single(leases, static lease => lease is not null);

            Assert.Single(failures, static failure => failure is OverflowException);

            Assert.Equal(long.MaxValue, ReadCensus(gate, work));

        }
        finally
        {

            count.SetValue(gate, (long)leases.Count(static lease => lease is not null));

            foreach (IAsyncDisposable? lease in leases)
            {

                if (lease is not null)
                {

                    await lease.DisposeAsync();

                }

            }

        }

        Assert.Equal(0, ReadCensus(gate, work));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(101));

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

    }

    [Fact]
    public async Task Cancelled_drain_call_does_not_cancel_the_exact_shared_closure_signal()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable request = AcquireLifetime(gate, work: false);

        Assert.Null(PublishedZeroSignal(gate));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(87));

        Assert.Null(PublishedZeroSignal(gate));

        using CancellationTokenSource cancelled = new();

        Task<Result> first = gate.DrainRequestAndWorkAsync(closing, cancelled.Token).AsTask();

        TaskCompletionSource signal = Assert.IsType<TaskCompletionSource>(PublishedZeroSignal(gate));

        Task<Result> second = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        Assert.Same(signal, PublishedZeroSignal(gate));

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.False(signal.Task.IsCompleted);

        Assert.False(second.IsCompleted);

        await request.DisposeAsync();

        Assert.True((await second.WaitAsync(BoundedWait)).IsSuccess);

        Assert.True(signal.Task.IsCompletedSuccessfully);

    }

    private static FieldInfo CensusField(bool work) => Assert.IsType<FieldInfo>(
        typeof(GrimoireConnectionAdmissionGate).GetField(work ? "_workCount" : "_requestCount", BindingFlags.Instance | BindingFlags.NonPublic), exactMatch: false);

    private static long ReadCensus(GrimoireConnectionAdmissionGate gate, bool work) =>
        Assert.IsType<long>(CensusField(work).GetValue(gate));

    private static object? PublishedZeroSignal(GrimoireConnectionAdmissionGate gate)
    {

        FieldInfo? field = typeof(GrimoireConnectionAdmissionGate)
            .GetField("_stageOneZeroSignal", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);

        return field.GetValue(gate);

    }

    private static IEnumerable<object> OwnedLifetimeObjects(object root)
    {

        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);

        Stack<object> pending = new();

        pending.Push(root);

        while (pending.TryPop(out object? value))
        {

            if (value is GrimoireConnectionAdmissionGate || !seen.Add(value))
            {

                continue;

            }

            yield return value;

            if (value.GetType().DeclaringType != typeof(GrimoireConnectionAdmissionGate))
            {

                continue;

            }

            foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {

                if (!field.FieldType.IsValueType && field.GetValue(value) is object child)
                {

                    pending.Push(child);

                }

            }

        }

    }

    private static void AllocateAndReleaseLifetime(GrimoireConnectionAdmissionGate gate, bool work)
    {

        if (work)
        {

            gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out IGrimoireWorkLease? lease);

            lease!.DisposeAsync().GetAwaiter().GetResult();

        }
        else
        {

            gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? lease);

            lease!.DisposeAsync().GetAwaiter().GetResult();

        }

    }

}
