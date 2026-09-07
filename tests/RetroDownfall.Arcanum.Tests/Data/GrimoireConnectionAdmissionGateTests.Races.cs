using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{

    [Theory]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, -1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public async Task Request_admission_and_close_have_one_census_order(byte value, int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        IGrimoireRequestLease? request = null;

        IGrimoireClosingOwner? owner = null;

        bool admitted = false;

        await RaceDedicated(
            () => admitted = gate.TryAcquireRequestLease((GrimoireRequestKind)value, out request),
            () => owner = Begin(gate, Owner(71)),
            order);

        await using IGrimoireClosingOwner closing = owner!;

        if (order != 0)
        {

            Assert.Equal(order == -1, admitted);

        }

        Assert.False(gate.TryAcquireRequestLease((GrimoireRequestKind)value, out _));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        if (admitted)
        {

            Assert.NotNull(request);

            Assert.Equal(1, request.Generation);

            Assert.Equal(value == 2, request.MaintenanceRevocation.IsCancellationRequested);

            Assert.False(drain.IsCompleted);

            await request.DisposeAsync();

        }
        else
        {

            Assert.Null(request);

        }

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

    }

    [Theory]
    [MemberData(nameof(HostedWorkKinds))]
    public async Task Every_work_kind_admission_and_close_have_one_census_order(object value)
    {

        GrimoireWorkKind kind = Assert.IsType<GrimoireWorkKind>(value);

        foreach (int order in new[] { -1, 0, 1 })
        {

            GrimoireConnectionAdmissionGate gate = CreateGate();

            IGrimoireWorkLease? work = null;

            IGrimoireClosingOwner? owner = null;

            bool admitted = false;

            await RaceDedicated(
                () => admitted = gate.TryAcquireWorkLease(kind, out work),
                () => owner = Begin(gate, Owner(72)),
                order);

            await using IGrimoireClosingOwner closing = owner!;

            if (order != 0)
            {

                Assert.Equal(order == -1, admitted);

            }

            Assert.False(gate.TryAcquireWorkLease(kind, out _));

            Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            if (admitted)
            {

                Assert.NotNull(work);

                Assert.Equal(1, work.Generation);

                Assert.True(work.MaintenanceRevocation.IsCancellationRequested);

                Assert.False(work.TryBeginExternalEffectGroup(out _));

                Assert.False(drain.IsCompleted);

                await work.DisposeAsync();

            }
            else
            {

                Assert.Null(work);

            }

            Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        }

    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Open_admission_and_stage_two_have_one_census_order(int order)
    {

        ManualTimeProvider clock = new();

        RecordingStageTwoDrain physicalDrain = new(block: false);

        GrimoireConnectionAdmissionGate gate = new(clock, physicalDrain, OpeningTimeout);

        using SqliteConnection connection = new();

        IGrimoireConnectionOpenTicket? ticket = null;

        IGrimoireClosingOwner? owner = null;

        Task<Result<IGrimoireExclusiveClosedLease>>? close = null;

        Exception? refusal = null;

        await RaceDedicated(
            () => refusal = Record.Exception(() => ticket = gate.AcquireOrdinaryOpen(connection)),
            () =>
            {

                owner = Begin(gate, Owner(73));

                close = gate.CloseConnectionAdmissionAsync(owner, CancellationToken.None).AsTask();

            },
            order);

        await using IGrimoireClosingOwner closing = owner!;

        Assert.Equal(2, gate.CurrentGeneration);

        if (order != 0)
        {

            Assert.Equal(order == -1, ticket is not null);

        }

        if (ticket is not null)
        {

            Assert.Null(refusal);

            Assert.Equal(1, ticket.Generation);

            Assert.False(close!.IsCompleted);

            Assert.Equal(0, physicalDrain.CallCount);

            Assert.Equal("Grimoire.StaleOpenGeneration", ticket.RevalidateAfterNativeOpen().Error.Code);

            Assert.Throws<InvalidOperationException>(() => ticket.Dispose());

            Assert.False(close.IsCompleted);

            ticket.MarkRefusedAfterOpen();

            ticket.Dispose();

        }
        else
        {

            Assert.IsType<GrimoireMaintenanceUnavailableException>(refusal);

        }

        Result<IGrimoireExclusiveClosedLease> closed = await close!.WaitAsync(BoundedWait);

        Assert.True(closed.IsSuccess);

        Assert.Equal(1, physicalDrain.CallCount);

        await closed.Value.DisposeAsync();

    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Effect_start_and_close_select_one_frontier_winner(int order)
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SagaExtraction, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease work = acquired!;

        IGrimoireExternalEffectGroup? group = null;

        IGrimoireClosingOwner? owner = null;

        bool effectWon = false;

        int callbacks = 0;

        using CancellationTokenRegistration registration = work.MaintenanceRevocation.Register(() => Interlocked.Increment(ref callbacks));

        await RaceDedicated(
            () => effectWon = work.TryBeginExternalEffectGroup(out group),
            () => owner = Begin(gate, Owner(74)),
            order);

        await using IGrimoireClosingOwner closing = owner!;

        if (order != 0)
        {

            Assert.Equal(order == -1, effectWon);

        }

        Assert.Equal(!effectWon, work.MaintenanceRevocation.IsCancellationRequested);

        Assert.Equal(effectWon ? 0 : 1, callbacks);

        Assert.False(work.TryBeginExternalEffectGroup(out _));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        if (effectWon)
        {

            Assert.NotNull(group);

            await work.DisposeAsync();

            Assert.False(drain.IsCompleted);

            await group.DisposeAsync();

        }
        else
        {

            Assert.Null(group);

            await work.DisposeAsync();

        }

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        Assert.Equal(1, callbacks);

    }

    // Both participants exist before the barrier opens. -1 and 1 force each legal ordering;
    // 0 releases competing calls together and checks whichever linearized outcome is observed.
    private static async Task RaceDedicated(Action first, Action second, int order)
    {

        using Barrier start = new(2);

        using ManualResetEventSlim firstDone = new();

        using ManualResetEventSlim secondDone = new();

        Task firstTask = StartDedicated(() =>
        {

            try
            {

                Assert.True(start.SignalAndWait(BoundedWait));

                if (order == 1)
                {

                    Assert.True(secondDone.Wait(BoundedWait));

                }

                first();

            }
            finally
            {

                firstDone.Set();

            }

        });

        Task secondTask = StartDedicated(() =>
        {

            try
            {

                Assert.True(start.SignalAndWait(BoundedWait));

                if (order == -1)
                {

                    Assert.True(firstDone.Wait(BoundedWait));

                }

                second();

            }
            finally
            {

                secondDone.Set();

            }

        });

        await Task.WhenAll(firstTask, secondTask).WaitAsync(BoundedWait + BoundedWait);

    }

}
