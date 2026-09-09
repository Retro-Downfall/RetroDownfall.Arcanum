using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class GrimoireConnectionAdmissionGateTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Nested_and_non_lifo_disposal_restore_only_live_finisher_ancestors(
        bool outerWork,
        bool innerWork,
        bool nonLifo)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable outer = AcquireLifetime(gate, outerWork);

        await using IAsyncDisposable middle = AcquireLifetime(gate, innerWork);

        await using IAsyncDisposable head = AcquireLifetime(gate, innerWork);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(63));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        AssertFinisherOpen(gate);

        await (nonLifo ? middle : head).DisposeAsync();

        AssertFinisherOpen(gate);

        await (nonLifo ? head : middle).DisposeAsync();

        AssertFinisherOpen(gate);

        Assert.False(drain.IsCompleted);

        await outer.DisposeAsync();

        AssertFinisherRefused(gate);

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flowed_finisher_observes_shared_release_without_gaining_an_independent_lifetime(bool work)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IAsyncDisposable original = AcquireLifetime(gate, work);

        TaskCompletionSource ready = NewBarrier();

        using ManualResetEventSlim closingStarted = new();

        TaskCompletionSource liveOpenObserved = NewBarrier();

        using ManualResetEventSlim originalReleased = new();

        Task child = StartDedicated(() =>
        {
            ready.TrySetResult();

            Assert.True(closingStarted.Wait(BoundedWait));

            AssertFinisherOpen(gate);

            liveOpenObserved.TrySetResult();

            Assert.True(originalReleased.Wait(BoundedWait));

            AssertFinisherRefused(gate);
        });

        try
        {
            await ready.Task.WaitAsync(BoundedWait);

            await using IGrimoireClosingOwner closing = Begin(gate, Owner(64));

            closingStarted.Set();

            await liveOpenObserved.Task.WaitAsync(BoundedWait);

            await original.DisposeAsync();

            originalReleased.Set();

            await child.WaitAsync(BoundedWait);

            Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);
        }
        finally
        {
            closingStarted.Set();

            originalReleased.Set();

            await child.WaitAsync(BoundedWait);
        }
    }

    [Fact]
    public async Task Promoted_request_cannot_replay_after_abort_or_remove_a_new_request_from_the_next_census()
    {
        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease promoted = acquired!;

        await using IAsyncDisposable blocker = AcquireLifetime(gate, work: true);

        using SqliteConnection exact = new();

        await using IGrimoireClosingOwner first = Begin(gate, Owner(65), promoted, exact);

        await TimeoutAndAbort(gate, clock, first);

        await blocker.DisposeAsync();

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? next));

        await using IGrimoireRequestLease current = next!;

        Result<IGrimoireClosingOwner> replay = gate.BeginOrResumeExclusive(Owner(66), promoted, exact);

        Assert.True(replay.IsFailure);

        Assert.Equal("Grimoire.AdmissionLifecycleConflict", replay.Error.Code);

        await using IGrimoireClosingOwner second = Begin(gate, Owner(66));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

        await promoted.DisposeAsync();

        await promoted.DisposeAsync();

        Assert.False(drain.IsCompleted);

        await current.DisposeAsync();

        AssertFinisherRefused(gate);

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);
    }

    [Fact]
    public async Task Unpromoted_old_request_cannot_be_promoted_out_of_the_next_generation_census()
    {
        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? acquired));

        await using IGrimoireRequestLease old = acquired!;

        Assert.Equal(1, old.Generation);

        await using IGrimoireClosingOwner first = Begin(gate, Owner(82));

        await TimeoutAndAbort(gate, clock, first);

        using SqliteConnection exact = new();

        using SqliteConnection foreign = new();

        Task<long> nextOpen = gate.WaitForNextOpenGenerationAsync(2, CancellationToken.None);

        Result<IGrimoireClosingOwner> stalePromotion = gate.BeginOrResumeExclusive(Owner(83), old, exact);

        Assert.True(stalePromotion.IsFailure);

        Assert.Equal("Grimoire.AdmissionLifecycleConflict", stalePromotion.Error.Code);

        Assert.Equal(2, gate.CurrentGeneration);

        Assert.False(nextOpen.IsCompleted);

        Assert.True(gate.TryAcquireRequestLease(GrimoireRequestKind.Finite, out IGrimoireRequestLease? currentRequest));

        await using IGrimoireRequestLease current = currentRequest!;

        Assert.Equal(2, current.Generation);

        await using IAsyncDisposable ordinaryWork = AcquireLifetime(gate, work: true);

        using (IGrimoireConnectionOpenTicket ordinaryOpen = gate.AcquireOrdinaryOpen(exact))
        {
            Assert.Equal(2, ordinaryOpen.Generation);

            ordinaryOpen.MarkFailed();
        }

        await ordinaryWork.DisposeAsync();

        await current.DisposeAsync();

        await using IGrimoireClosingOwner second = Begin(gate, Owner(84));

        Assert.Equal(2, second.Generation);

        Assert.Throws<GrimoireMaintenanceUnavailableException>(() => gate.AcquireOrdinaryOpen(exact));

        Assert.Throws<GrimoireMaintenanceUnavailableException>(() => gate.AcquireOrdinaryOpen(foreign));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        Result<IGrimoireExclusiveClosedLease> prematureClose = await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None);

        Assert.True(prematureClose.IsFailure);

        Assert.Equal("Grimoire.AdmissionLifecycleConflict", prematureClose.Error.Code);

        await old.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None);

        Assert.True(closed.IsSuccess);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Assert.Equal(3, lease.Generation);

        Assert.False(nextOpen.IsCompleted);

        Assert.True((await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

        Assert.Equal(3, await nextOpen.WaitAsync(BoundedWait));
    }

    [Fact]
    public async Task Revoked_old_work_remains_in_the_next_close_census_after_proven_abort()
    {
        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.EntryWeaving, out IGrimoireWorkLease? acquired));

        await using IGrimoireWorkLease old = acquired!;

        await using IGrimoireClosingOwner first = Begin(gate, Owner(67));

        await TimeoutAndAbort(gate, clock, first);

        Assert.True(old.MaintenanceRevocation.IsCancellationRequested);

        Assert.False(old.TryBeginExternalEffectGroup(out _));

        await using IGrimoireClosingOwner second = Begin(gate, Owner(68));

        AssertFinisherRefused(gate);

        Task<Result> drain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

        Assert.False(drain.IsCompleted);

        Assert.True((await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None)).IsFailure);

        await old.DisposeAsync();

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(second, CancellationToken.None);

        Assert.True(closed.IsSuccess);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Assert.Equal(3, lease.Generation);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Repeated_old_generation_disposal_cannot_release_a_new_generation_lifetime(bool oldWork, bool newWork)
    {
        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        await using IAsyncDisposable old = AcquireLifetime(gate, oldWork);

        await using IGrimoireClosingOwner first = Begin(gate, Owner(69));

        await TimeoutAndAbort(gate, clock, first);

        await using IAsyncDisposable current = AcquireLifetime(gate, newWork);

        await old.DisposeAsync();

        await old.DisposeAsync();

        await using IGrimoireClosingOwner second = Begin(gate, Owner(70));

        Task<Result> drain = gate.DrainRequestAndWorkAsync(second, CancellationToken.None).AsTask();

        await old.DisposeAsync();

        Assert.False(drain.IsCompleted);

        AssertFinisherOpen(gate);

        await current.DisposeAsync();

        AssertFinisherRefused(gate);

        Assert.True((await drain.WaitAsync(BoundedWait)).IsSuccess);
    }

    private static async Task TimeoutAndAbort(
        GrimoireConnectionAdmissionGate gate,
        ManualTimeProvider clock,
        IGrimoireClosingOwner closing)
    {
        Task<Result> drain = gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

        await clock.WaitForScheduledTimerCountAsync(1).WaitAsync(BoundedWait);

        clock.Advance(OpeningTimeout);

        Assert.Equal("Grimoire.WorkDrainTimeout", (await drain.WaitAsync(BoundedWait)).Error.Code);

        Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Equal(2, gate.CurrentGeneration);
    }

    private static void AssertFinisherOpen(GrimoireConnectionAdmissionGate gate)
    {
        using SqliteConnection connection = new();

        using IGrimoireConnectionOpenTicket ticket = gate.AcquireOrdinaryOpen(connection);

        Assert.True(ticket.RevalidateAfterNativeOpen().IsSuccess);

        Assert.True(ticket.MarkOpened().IsSuccess);
    }

    private static void AssertFinisherRefused(GrimoireConnectionAdmissionGate gate)
    {
        using SqliteConnection connection = new();

        Assert.Throws<GrimoireMaintenanceUnavailableException>(() => gate.AcquireOrdinaryOpen(connection));
    }
}
