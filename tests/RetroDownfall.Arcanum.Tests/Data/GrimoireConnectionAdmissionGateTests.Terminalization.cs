using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{
    [Theory]
    [InlineData(0, -1)]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, -1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(3, -1)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    public async Task Final_lifetime_release_cannot_miss_a_concurrent_drain_waiter(int lifetimeKind, int order)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        IAsyncDisposable terminal;

        if (lifetimeKind < 2)
        {
            GrimoireRequestKind kind = lifetimeKind == 0 ? GrimoireRequestKind.Finite : GrimoireRequestKind.QuiesceableStream;

            Assert.True(gate.TryAcquireRequestLease(kind, out IGrimoireRequestLease? request));

            terminal = request!;
        }
        else
        {
            Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? work));

            terminal = work!;

            if (lifetimeKind == 3)
            {
                Assert.True(work!.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));

                await work.DisposeAsync();

                terminal = group!;
            }
        }

        await using IAsyncDisposable release = terminal;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(75));

        Task<Result>? drain = null;

        await RaceDedicated(
            () =>
            {
                drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

                if (order == -1)
                {
                    Assert.False(drain.IsCompleted);
                }
            },
            () => release.DisposeAsync().GetAwaiter().GetResult(),
            order);

        Assert.True((await drain!.WaitAsync(BoundedWait)).IsSuccess);

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess);

        await closed.Value.DisposeAsync();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Final_open_terminalization_cannot_miss_a_concurrent_stage_two_waiter(int order)
    {
        RecordingStageTwoDrain physicalDrain = new(block: false);

        GrimoireConnectionAdmissionGate gate = new(new ManualTimeProvider(), physicalDrain, OpeningTimeout);

        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(76));

        Task<Result<IGrimoireExclusiveClosedLease>>? close = null;

        await RaceDedicated(
            () =>
            {
                close = gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None).AsTask();

                if (order == -1)
                {
                    Assert.False(close.IsCompleted);

                    Assert.Equal(0, physicalDrain.CallCount);
                }
            },
            ticket.MarkFailed,
            order);

        Result<IGrimoireExclusiveClosedLease> closed = await close!.WaitAsync(BoundedWait);

        Assert.True(closed.IsSuccess);

        Assert.Equal(1, physicalDrain.CallCount);

        await closed.Value.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_lifetime_disposal_is_exactly_once_and_cannot_release_a_sibling(bool work)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable first = AcquireLifetime(gate, work);

        await using IAsyncDisposable sibling = AcquireLifetime(gate, work);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(77));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await RaceDedicated(
            () => first.DisposeAsync().GetAwaiter().GetResult(),
            () => first.DisposeAsync().GetAwaiter().GetResult(),
            order: 0);

        await first.DisposeAsync();

        Assert.False(drain.IsCompleted);

        await sibling.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        await sibling.DisposeAsync();

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Disposed_effect_group_cannot_release_a_successor_group_on_the_same_work_lease()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? oldGroup));

        await oldGroup!.DisposeAsync();

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? acquiredGroup));

        await using IGrimoireExternalEffectGroup current = acquiredGroup!;

        await RaceDedicated(
            () => oldGroup.DisposeAsync().GetAwaiter().GetResult(),
            () => oldGroup.DisposeAsync().GetAwaiter().GetResult(),
            order: 0);

        Assert.False(work.TryBeginExternalEffectGroup(out _));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(78));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await work.DisposeAsync();

        await oldGroup.DisposeAsync();

        Assert.False(drain.IsCompleted);

        Assert.False(work.MaintenanceRevocation.IsCancellationRequested);

        await current.DisposeAsync();

        Assert.True(work.MaintenanceRevocation.IsCancellationRequested);

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Scope_and_group_disposal_drain_and_revoke_exactly_once_in_either_order(int order)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        Assert.True(work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? acquiredGroup));

        await using IGrimoireExternalEffectGroup group = acquiredGroup!;

        int callbacks = 0;

        using CancellationTokenRegistration registration = work.MaintenanceRevocation.Register(() => Interlocked.Increment(ref callbacks));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(79));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await RaceDedicated(
            () => work.DisposeAsync().GetAwaiter().GetResult(),
            () => group.DisposeAsync().GetAwaiter().GetResult(),
            order);

        await work.DisposeAsync();

        await group.DisposeAsync();

        Assert.Equal(1, callbacks);

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Terminal_open_disposal_and_replay_cannot_release_a_new_generation_ticket(int outcome)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        using SqliteConnection oldConnection = new();

        using IGrimoireConnectionOpenTicket old = gate.AcquireOrdinaryOpen(oldConnection);

        if (outcome == 0)
        {
            Assert.True(old.RevalidateAfterNativeOpen().IsSuccess);

            Assert.True(old.MarkOpened().IsSuccess);
        }
        else if (outcome == 1)
        {
            old.MarkFailed();
        }

        await using IGrimoireClosingOwner first = Begin(gate, Owner(80));

        Task<Result<IGrimoireExclusiveClosedLease>> firstClose = gate.CloseConnectionAdmissionAsync(first, CancellationToken.None).AsTask();

        if (outcome == 2)
        {
            Assert.True(old.RevalidateAfterNativeOpen().IsFailure);

            Assert.Throws<InvalidOperationException>(() => old.Dispose());

            Assert.False(firstClose.IsCompleted);

            old.MarkRefusedAfterOpen();
        }

        Result<IGrimoireExclusiveClosedLease> firstClosed = await firstClose.WaitAsync(BoundedWait);

        Assert.True(firstClosed.IsSuccess);

        await using IGrimoireExclusiveClosedLease firstLease = firstClosed.Value;

        Assert.True((await firstLease.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

        using SqliteConnection currentConnection = new();

        using IGrimoireConnectionOpenTicket current = gate.AcquireOrdinaryOpen(currentConnection);

        Assert.Equal(2, current.Generation);

        await using IGrimoireClosingOwner second = Begin(gate, Owner(81));

        Task<Result<IGrimoireExclusiveClosedLease>> secondClose = gate.CloseConnectionAdmissionAsync(second, CancellationToken.None).AsTask();

        try
        {
            Assert.Throws<InvalidOperationException>(() => old.MarkFailed());

            Assert.Throws<InvalidOperationException>(() => old.MarkOpened());

            Assert.Throws<InvalidOperationException>(() => old.MarkRefusedAfterOpen());

            Assert.Throws<InvalidOperationException>(() => old.RevalidateAfterNativeOpen());

            await RaceDedicated(old.Dispose, old.Dispose, order: 0);

            Assert.Throws<ObjectDisposedException>(() => old.MarkFailed());

            Assert.Throws<ObjectDisposedException>(() => old.MarkOpened());

            Assert.Throws<ObjectDisposedException>(() => old.MarkRefusedAfterOpen());

            Assert.Throws<ObjectDisposedException>(() => old.RevalidateAfterNativeOpen());

            Assert.False(secondClose.IsCompleted);
        }
        finally
        {
            current.MarkFailed();
        }

        Result<IGrimoireExclusiveClosedLease> secondClosed = await secondClose.WaitAsync(BoundedWait);

        Assert.True(secondClosed.IsSuccess);

        Assert.Equal(3, secondClosed.Value.Generation);

        await secondClosed.Value.DisposeAsync();
    }
}
