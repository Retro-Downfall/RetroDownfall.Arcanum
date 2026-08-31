using System.Data.Common;

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
    public async Task Precancelled_connection_close_does_not_issue_a_lease_when_no_open_is_unresolved()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(9));

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.CloseConnectionAdmissionAsync(closing, cancelled.Token).AsTask());

        Result<IGrimoireExclusiveClosedLease> resumed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = resumed.Value;

        Result keptClosed = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

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
    public async Task Precancelled_next_open_generation_wait_is_cancelled_when_generation_is_already_open()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        long observedGeneration = gate.CurrentGeneration;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(10));

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result reopened = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        Task<long> nextOpen = gate.WaitForNextOpenGenerationAsync(
            observedGeneration,
            cancelled.Token);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextOpen);

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

    [Fact]
    public async Task Exclusive_waits_for_another_request_through_async_scope_disposal()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? initiating));

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? other));

        TaskCompletionSource databaseHolderDisposed = NewBarrier();

        TaskCompletionSource allowScopeDisposal = NewBarrier();

        AsyncScopeDisposalSentinel scope = new(
            other!,
            databaseHolderDisposed,
            allowScopeDisposal);

        using SqliteConnection initiatingConnection = new();

        await using IGrimoireClosingOwner closing = Begin(
            gate,
            Owner(11),
            initiating,
            initiatingConnection);

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await initiating!.DisposeAsync();

        Task scopeDisposal = scope.DisposeAsync().AsTask();

        await databaseHolderDisposed.Task;

        Assert.False(drain.IsCompleted);

        allowScopeDisposal.TrySetResult();

        await scopeDisposal;

        Result drained = await drain;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task Promotion_removes_only_the_exact_owner_matched_initiating_request()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? initiating));

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? other));

        using SqliteConnection connection = new();

        await using IGrimoireClosingOwner closing = Begin(
            gate,
            Owner(12),
            initiating,
            connection);

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await initiating!.DisposeAsync();

        Assert.False(drain.IsCompleted);

        await other!.DisposeAsync();

        Result drained = await drain;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task Promotion_rejects_another_request_owner_or_connection()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? initiating));

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? other));

        CovenantExclusiveRecoveryOwner owner = Owner(13);

        using SqliteConnection connection = new();

        using SqliteConnection anotherConnection = new();

        await using IGrimoireClosingOwner closing = Begin(
            gate,
            owner,
            initiating,
            connection);

        Result<IGrimoireClosingOwner> anotherRequest = gate.BeginOrResumeExclusive(
            owner,
            other,
            connection);

        Result<IGrimoireClosingOwner> anotherOwner = gate.BeginOrResumeExclusive(
            Owner(14),
            initiating,
            connection);

        Result<IGrimoireClosingOwner> anotherConnectionResult = gate.BeginOrResumeExclusive(
            owner,
            initiating,
            anotherConnection);

        Assert.True(anotherRequest.IsFailure);

        Assert.True(anotherOwner.IsFailure);

        Assert.True(anotherConnectionResult.IsFailure);

        await initiating!.DisposeAsync();

        await other!.DisposeAsync();

    }

    [Fact]
    public async Task Revocation_wins_effect_race_and_provider_frontier_cannot_start()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.EntryWeaving,
            out IGrimoireWorkLease? work));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(15));

        Assert.True(work!.MaintenanceRevocation.IsCancellationRequested);

        Assert.False(work.TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup));

        Assert.Null(effectGroup);

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await work.DisposeAsync();

        Result drained = await drain;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task Effect_start_wins_race_and_closure_waits_through_durable_disposition()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.SagaExtraction,
            out IGrimoireWorkLease? work));

        Assert.True(work!.TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup));

        Assert.False(work.TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? concurrentEffectGroup));

        Assert.Null(concurrentEffectGroup);

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(16));

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await work.DisposeAsync();

        Assert.False(drain.IsCompleted);

        await effectGroup!.DisposeAsync();

        Result drained = await drain;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task Denied_work_waits_for_a_later_open_generation_without_spinning()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        long observedGeneration = gate.CurrentGeneration;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(17));

        Assert.False(gate.TryAcquireWorkLease(
            GrimoireWorkKind.SessionAttachmentIndexing,
            out IGrimoireWorkLease? denied));

        Assert.Null(denied);

        Task<long> nextOpen = gate.WaitForNextOpenGenerationAsync(
            observedGeneration,
            CancellationToken.None);

        Assert.False(nextOpen.IsCompleted);

        Result drained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease lease = closed.Value;

        Result reopened = await lease.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        Assert.Equal(observedGeneration + 1, await nextOpen);

    }

    [Fact]
    public async Task Stage_one_open_requires_the_exact_still_live_finisher_lifetime()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.QuiesceableStream,
            out IGrimoireRequestLease? request));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(18));

        Assert.True(request!.MaintenanceRevocation.IsCancellationRequested);

        using SqliteConnection finisherConnection = new();

        using IGrimoireConnectionOpenTicket finisher =
            gate.AcquireOrdinaryOpen(finisherConnection);

        finisher.MarkFailed();

        Task<Exception?> unrelatedOpen;

        using (ExecutionContext.SuppressFlow())
        {

            unrelatedOpen = Task.Run<Exception?>(
                () => Record.Exception(
                    () => gate.AcquireOrdinaryOpen(new SqliteConnection()).Dispose()));

        }

        _ = Assert.IsType<GrimoireMaintenanceUnavailableException>(await unrelatedOpen);

        await request.DisposeAsync();

        using SqliteConnection releasedConnection = new();

        _ = Assert.Throws<GrimoireMaintenanceUnavailableException>(
            () => gate.AcquireOrdinaryOpen(releasedConnection));

    }

    [Fact]
    public async Task Stage_one_timeout_stays_closing_denies_new_work_allows_finisher_opens_and_starts_no_destructive_work()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.EntryWeaving,
            out IGrimoireWorkLease? work));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(19));

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await clock.WaitForScheduledTimerCountAsync(1);

        clock.Advance(OpeningTimeout);

        Result timedOut = await drain;

        Assert.True(timedOut.IsFailure);

        Assert.False(gate.TryAcquireWorkLease(
            GrimoireWorkKind.EntryWeaving,
            out IGrimoireWorkLease? denied));

        Assert.Null(denied);

        using SqliteConnection finisherConnection = new();

        using IGrimoireConnectionOpenTicket finisher =
            gate.AcquireOrdinaryOpen(finisherConnection);

        finisher.MarkFailed();

        Assert.False(work!.TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup));

        Assert.Null(effectGroup);

        Result<IGrimoireExclusiveClosedLease> destructive =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(destructive.IsFailure);

        await work.DisposeAsync();

    }

    [Fact]
    public async Task The_same_owner_can_resume_a_timed_out_stage_one_transition()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? request));

        CovenantExclusiveRecoveryOwner owner = Owner(20);

        IGrimoireClosingOwner first = Begin(gate, owner);

        Task<Result> firstDrain = gate
            .DrainRequestAndWorkAsync(first, CancellationToken.None)
            .AsTask();

        await clock.WaitForScheduledTimerCountAsync(1);

        clock.Advance(OpeningTimeout);

        Assert.True((await firstDrain).IsFailure);

        await first.DisposeAsync();

        await using IGrimoireClosingOwner resumed = Begin(gate, owner);

        await request!.DisposeAsync();

        Result drained = await gate.DrainRequestAndWorkAsync(
            resumed,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(resumed, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await closed.Value.DisposeAsync();

    }

    [Fact]
    public async Task Only_proven_pre_erasure_safety_can_abort_a_timed_out_stage_one_transition()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? request));

        long observedGeneration = gate.CurrentGeneration;

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(21));

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await clock.WaitForScheduledTimerCountAsync(1);

        clock.Advance(OpeningTimeout);

        Assert.True((await drain).IsFailure);

        Result unsafeAbort = await gate.AbortClosingAsync(
            closing,
            static _ => ValueTask.FromResult(false),
            CancellationToken.None);

        Assert.True(unsafeAbort.IsFailure);

        Assert.False(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? denied));

        Assert.Null(denied);

        await request!.DisposeAsync();

        Result safeAbort = await gate.AbortClosingAsync(
            closing,
            static _ => ValueTask.FromResult(true),
            CancellationToken.None);

        Assert.True(safeAbort.IsSuccess, safeAbort.IsFailure ? safeAbort.Error.Message : null);

        Assert.Equal(
            observedGeneration + 1,
            await gate.WaitForNextOpenGenerationAsync(
                observedGeneration,
                CancellationToken.None));

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? admitted));

        await admitted!.DisposeAsync();

    }

    [Fact]
    public async Task Revoked_work_cannot_start_effect_after_proven_stage_one_abort()
    {

        ManualTimeProvider clock = new();

        GrimoireConnectionAdmissionGate gate = CreateGate(clock);

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.EntryWeaving,
            out IGrimoireWorkLease? revokedWork));

        await using IGrimoireClosingOwner closing = Begin(gate, Owner(22));

        Task<Result> drain = gate
            .DrainRequestAndWorkAsync(closing, CancellationToken.None)
            .AsTask();

        await clock.WaitForScheduledTimerCountAsync(1);

        clock.Advance(OpeningTimeout);

        Assert.True((await drain).IsFailure);

        Result aborted = await gate.AbortClosingAsync(
            closing,
            static _ => ValueTask.FromResult(true),
            CancellationToken.None);

        Assert.True(aborted.IsSuccess, aborted.IsFailure ? aborted.Error.Message : null);

        Assert.True(revokedWork!.MaintenanceRevocation.IsCancellationRequested);

        Assert.False(revokedWork.TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? staleEffect));

        Assert.Null(staleEffect);

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.EntryWeaving,
            out IGrimoireWorkLease? newGenerationWork));

        Assert.True(newGenerationWork!.TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? newGenerationEffect));

        await newGenerationEffect!.DisposeAsync();

        await newGenerationWork.DisposeAsync();

        await revokedWork.DisposeAsync();

    }

    [Fact]
    public async Task Throwing_revocation_callbacks_do_not_prevent_all_signals_or_exact_closing_token()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        Assert.True(gate.TryAcquireRequestLease(
            GrimoireRequestKind.QuiesceableStream,
            out IGrimoireRequestLease? request));

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.SessionAttachmentIndexing,
            out IGrimoireWorkLease? first));

        Assert.True(gate.TryAcquireWorkLease(
            GrimoireWorkKind.SagaExtraction,
            out IGrimoireWorkLease? second));

        using CancellationTokenRegistration requestThrowingCallback =
            request!.MaintenanceRevocation.Register(
                static () => throw new InvalidOperationException("request callback failure"));

        using CancellationTokenRegistration firstThrowingCallback =
            first!.MaintenanceRevocation.Register(
                static () => throw new InvalidOperationException("first callback failure"));

        using CancellationTokenRegistration secondThrowingCallback =
            second!.MaintenanceRevocation.Register(
                static () => throw new InvalidOperationException("second callback failure"));

        CovenantExclusiveRecoveryOwner owner = Owner(23);

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(owner);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        await using IGrimoireClosingOwner closing = begun.Value;

        Assert.True(request.MaintenanceRevocation.IsCancellationRequested);

        Assert.True(first.MaintenanceRevocation.IsCancellationRequested);

        Assert.True(second.MaintenanceRevocation.IsCancellationRequested);

        Result<IGrimoireClosingOwner> resumed = gate.BeginOrResumeExclusive(owner);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        Assert.Same(closing, resumed.Value);

        await request.DisposeAsync();

        await first.DisposeAsync();

        await second.DisposeAsync();

        Result drained = await gate.DrainRequestAndWorkAsync(
            closing,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

    }

    [Fact]
    public async Task Scoped_permit_authorizes_only_the_exact_connection_owner_and_generation()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(24);

        using SqliteConnection exactConnection = new();

        using SqliteConnection foreignConnection = new();

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        await using IGrimoireScopedConnectionPermit permit =
            closed.AcquireScopedConnectionPermit(exactConnection).Value;

        await using IGrimoireMaintenanceIoLane lane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        Result<IGrimoireTrackedMaintenanceHandle> foreignConnectionResult =
            permit.AcquireOpen(
                foreignConnection,
                owner,
                closed.Generation,
                lane);

        Result<IGrimoireTrackedMaintenanceHandle> foreignOwnerResult =
            permit.AcquireOpen(
                exactConnection,
                Owner(25),
                closed.Generation,
                lane);

        Result<IGrimoireTrackedMaintenanceHandle> foreignGenerationResult =
            permit.AcquireOpen(
                exactConnection,
                owner,
                checked(closed.Generation + 1),
                lane);

        Assert.True(foreignConnectionResult.IsFailure);

        Assert.True(foreignOwnerResult.IsFailure);

        Assert.True(foreignGenerationResult.IsFailure);

        Result<IGrimoireTrackedMaintenanceHandle> first = permit.AcquireOpen(
            exactConnection,
            owner,
            closed.Generation,
            lane);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        Assert.True(first.Value.ReportNotOpened().IsSuccess);

        Result<IGrimoireTrackedMaintenanceHandle> reopened = permit.AcquireOpen(
            exactConnection,
            owner,
            closed.Generation,
            lane);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);

        Result refusedWhileOpen = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(refusedWhileOpen.IsFailure);

        Assert.True(reopened.Value.ReportPhysicallyClosed().IsSuccess);

        await permit.DisposeAsync();

        Result keptClosed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Factory_capability_is_one_shot_and_rejects_path_mode_or_purpose_widening()
    {

        const string CanonicalPath = "/var/lib/arcanum/grimoire.db";

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(26);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        await using IGrimoireMaintenanceIoLane lane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        IGrimoireMaintenanceConnectionCapability widenedPath =
            closed.IssueMaintenanceConnectionCapability(
                CanonicalPath,
                CovenantMaintenanceConnectionMode.ReadOnly,
                CovenantMaintenanceConnectionPurpose.IntegrityVerification).Value;

        Result<IGrimoireTrackedMaintenanceHandle> wrongPath = widenedPath.Consume(
            owner,
            closed.Generation,
            CanonicalPath + ".copy",
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Result<IGrimoireTrackedMaintenanceHandle> spentAfterWrongPath = widenedPath.Consume(
            owner,
            closed.Generation,
            CanonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Assert.True(wrongPath.IsFailure);

        Assert.True(spentAfterWrongPath.IsFailure);

        await widenedPath.DisposeAsync();

        await AssertCapabilityMismatchConsumesAsync(
            closed,
            lane,
            owner,
            CanonicalPath,
            CovenantMaintenanceConnectionMode.ReadWrite,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification);

        await AssertCapabilityMismatchConsumesAsync(
            closed,
            lane,
            owner,
            CanonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.SidecarProof);

        await AssertCapabilityIdentityMismatchConsumesAsync(
            closed,
            lane,
            Owner(27),
            closed.Generation,
            CanonicalPath);

        await AssertCapabilityIdentityMismatchConsumesAsync(
            closed,
            lane,
            owner,
            checked(closed.Generation + 1),
            CanonicalPath);

        await using IGrimoireMaintenanceConnectionCapability capability =
            closed.IssueMaintenanceConnectionCapability(
                CanonicalPath,
                CovenantMaintenanceConnectionMode.ReadOnly,
                CovenantMaintenanceConnectionPurpose.IntegrityVerification).Value;

        Result<IGrimoireTrackedMaintenanceHandle> consumed = capability.Consume(
            owner,
            closed.Generation,
            CanonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Result<IGrimoireTrackedMaintenanceHandle> reused = capability.Consume(
            owner,
            closed.Generation,
            CanonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Assert.True(consumed.IsSuccess, consumed.IsFailure ? consumed.Error.Message : null);

        Assert.True(reused.IsFailure);

        Task laneDisposal = lane.DisposeAsync().AsTask();

        Assert.False(laneDisposal.IsCompleted);

        Assert.True(consumed.Value.ReportPhysicallyClosed().IsSuccess);

        await laneDisposal;

        Result keptClosed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Renewal_ticket_is_one_shot_owner_generation_bound_and_physically_tracked()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(27);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        await using IGrimoireMaintenanceIoLane lane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        await using IGrimoireMaintenanceRenewalTicket wrongOwnerTicket =
            closed.IssueMaintenanceRenewalTicket().Value;

        Result refusedWhileTicketIsLive = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(refusedWhileTicketIsLive.IsFailure);

        Result<IGrimoireTrackedMaintenanceHandle> wrongOwner =
            wrongOwnerTicket.Consume(Owner(28), closed.Generation, lane);

        Result<IGrimoireTrackedMaintenanceHandle> spentWrongOwner =
            wrongOwnerTicket.Consume(owner, closed.Generation, lane);

        Assert.True(wrongOwner.IsFailure);

        Assert.True(spentWrongOwner.IsFailure);

        await using IGrimoireMaintenanceRenewalTicket wrongGenerationTicket =
            closed.IssueMaintenanceRenewalTicket().Value;

        Result<IGrimoireTrackedMaintenanceHandle> wrongGeneration =
            wrongGenerationTicket.Consume(
                owner,
                checked(closed.Generation + 1),
                lane);

        Result<IGrimoireTrackedMaintenanceHandle> spentWrongGeneration =
            wrongGenerationTicket.Consume(owner, closed.Generation, lane);

        Assert.True(wrongGeneration.IsFailure);

        Assert.True(spentWrongGeneration.IsFailure);

        await using IGrimoireMaintenanceRenewalTicket ticket =
            closed.IssueMaintenanceRenewalTicket().Value;

        Result<IGrimoireTrackedMaintenanceHandle> renewal =
            ticket.Consume(owner, closed.Generation, lane);

        Result<IGrimoireTrackedMaintenanceHandle> reused =
            ticket.Consume(owner, closed.Generation, lane);

        Assert.True(renewal.IsSuccess, renewal.IsFailure ? renewal.Error.Message : null);

        Assert.True(reused.IsFailure);

        Result refusedWhileRenewalIsLive = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(refusedWhileRenewalIsLive.IsFailure);

        Assert.True(renewal.Value.ReportPhysicallyClosed().IsSuccess);

        Result keptClosed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None);

        Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

    }

    [Fact]
    public async Task Maintenance_lane_wins_first_and_blocks_expired_owner_adoption()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(29);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        await using IGrimoireMaintenanceIoLane lane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)).Value;

        TaskCompletionSource adoptionRevalidated = NewBarrier();

        Task<Result<IGrimoireExpiredLeaseAdoptionInterlock>> adoption = gate
            .AcquireExpiredLeaseAdoptionInterlockAsync(
                owner,
                (candidate, _) =>
                {

                    Assert.Equal(owner, candidate);

                    adoptionRevalidated.TrySetResult();

                    return ValueTask.FromResult(true);

                },
                CancellationToken.None)
            .AsTask();

        Assert.False(adoption.IsCompleted);

        Assert.False(adoptionRevalidated.Task.IsCompleted);

        await lane.DisposeAsync();

        Result<IGrimoireExpiredLeaseAdoptionInterlock> adopted = await adoption;

        Assert.True(adopted.IsSuccess, adopted.IsFailure ? adopted.Error.Message : null);

        await using IGrimoireExpiredLeaseAdoptionInterlock interlock = adopted.Value;

        Assert.True(adoptionRevalidated.Task.IsCompletedSuccessfully);

    }

    [Fact]
    public async Task Adoption_wins_first_and_incumbent_cannot_enter_a_sensitive_phase()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(30);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        Result<IGrimoireExpiredLeaseAdoptionInterlock> acquiredAdoption =
            await gate.AcquireExpiredLeaseAdoptionInterlockAsync(
                owner,
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None);

        Assert.True(
            acquiredAdoption.IsSuccess,
            acquiredAdoption.IsFailure ? acquiredAdoption.Error.Message : null);

        IGrimoireExpiredLeaseAdoptionInterlock adoption = acquiredAdoption.Value;

        bool incumbentStillOwnsDurableLease = true;

        bool sensitivePhaseStarted = false;

        Task<Result<IGrimoireMaintenanceIoLane>> incumbent = closed
            .AcquireMaintenanceIoLaneAsync(
                (_, _, _) => ValueTask.FromResult(incumbentStillOwnsDurableLease),
                CancellationToken.None)
            .AsTask();

        Assert.False(incumbent.IsCompleted);

        incumbentStillOwnsDurableLease = false;

        await adoption.DisposeAsync();

        Result<IGrimoireMaintenanceIoLane> rejected = await incumbent;

        if (rejected.IsSuccess)
        {

            sensitivePhaseStarted = true;

            await rejected.Value.DisposeAsync();

        }

        Assert.True(rejected.IsFailure);

        Assert.False(sensitivePhaseStarted);

    }

    [Fact]
    public async Task Adoption_interlock_can_be_held_through_reopen_and_terminal_CAS()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(31);

        Result<IGrimoireExpiredLeaseAdoptionInterlock> acquiredAdoption =
            await gate.AcquireExpiredLeaseAdoptionInterlockAsync(
                owner,
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None);

        Assert.True(
            acquiredAdoption.IsSuccess,
            acquiredAdoption.IsFailure ? acquiredAdoption.Error.Message : null);

        IGrimoireExpiredLeaseAdoptionInterlock adoption = acquiredAdoption.Value;

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        Task<Result<IGrimoireMaintenanceIoLane>> competingLane = closed
            .AcquireMaintenanceIoLaneAsync(
                static (_, _, _) => ValueTask.FromResult(true),
                CancellationToken.None)
            .AsTask();

        Assert.False(competingLane.IsCompleted);

        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.IsFailure ? reopened.Error.Message : null);


        TaskCompletionSource terminalCasCompleted = NewBarrier();

        terminalCasCompleted.TrySetResult();

        Assert.False(competingLane.IsCompleted);

        Assert.True(terminalCasCompleted.Task.IsCompletedSuccessfully);

        await adoption.DisposeAsync();

        Result<IGrimoireMaintenanceIoLane> staleLane = await competingLane;

        Assert.True(staleLane.IsFailure);

    }

    [Fact]
    public async Task Overrun_step_selects_KeepClosed_before_any_next_phase()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(32);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        bool durableOwnerIsCurrent = true;

        await using IGrimoireMaintenanceIoLane lane =
            (await closed.AcquireMaintenanceIoLaneAsync(
                (_, _, _) => ValueTask.FromResult(durableOwnerIsCurrent),
                CancellationToken.None)).Value;

        durableOwnerIsCurrent = false;

        Result afterStep = await lane.RevalidateDurableOwnerAsync(
            (_, _, _) => ValueTask.FromResult(durableOwnerIsCurrent),
            CancellationToken.None);

        bool nextPhaseStarted = false;

        if (afterStep.IsSuccess)
        {

            nextPhaseStarted = true;

        }
        else
        {

            Result keptClosed = await closed.CompleteAsync(
                CovenantExclusiveLeaseDisposition.KeepClosed,
                CancellationToken.None);

            Assert.True(keptClosed.IsSuccess, keptClosed.IsFailure ? keptClosed.Error.Message : null);

        }

        Assert.True(afterStep.IsFailure);

        Assert.False(nextPhaseStarted);

        Assert.False(gate.TryAcquireRequestLease(
            GrimoireRequestKind.Finite,
            out IGrimoireRequestLease? denied));

        Assert.Null(denied);

    }

    [Fact]
    public async Task Precancelled_interlock_entry_never_revalidates_durable_ownership()
    {

        GrimoireConnectionAdmissionGate gate = CreateGate();

        CovenantExclusiveRecoveryOwner owner = Owner(33);

        await using IGrimoireExclusiveClosedLease closed = await Close(gate, owner);

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        bool laneRevalidated = false;

        bool adoptionRevalidated = false;

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => closed.AcquireMaintenanceIoLaneAsync(
                (_, _, _) =>
                {

                    laneRevalidated = true;

                    return ValueTask.FromResult(true);

                },
                cancelled.Token).AsTask());

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.AcquireExpiredLeaseAdoptionInterlockAsync(
                owner,
                (_, _) =>
                {

                    adoptionRevalidated = true;

                    return ValueTask.FromResult(true);

                },
                cancelled.Token).AsTask());

        Assert.False(laneRevalidated);

        Assert.False(adoptionRevalidated);

    }

    private static GrimoireConnectionAdmissionGate CreateGate(TimeProvider? timeProvider = null) =>
        new(timeProvider ?? TimeProvider.System, OpeningTimeout);

    private static async Task<IGrimoireExclusiveClosedLease> Close(
        GrimoireConnectionAdmissionGate gate,
        CovenantExclusiveRecoveryOwner owner)
    {

        await using IGrimoireClosingOwner closing = Begin(gate, owner);

        Result<IGrimoireExclusiveClosedLease> closed =
            await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;

    }

    private static async Task AssertCapabilityMismatchConsumesAsync(
        IGrimoireExclusiveClosedLease closed,
        IGrimoireMaintenanceIoLane lane,
        CovenantExclusiveRecoveryOwner owner,
        string canonicalPath,
        CovenantMaintenanceConnectionMode actualMode,
        CovenantMaintenanceConnectionPurpose actualPurpose)
    {

        await using IGrimoireMaintenanceConnectionCapability capability =
            closed.IssueMaintenanceConnectionCapability(
                canonicalPath,
                CovenantMaintenanceConnectionMode.ReadOnly,
                CovenantMaintenanceConnectionPurpose.IntegrityVerification).Value;

        Result<IGrimoireTrackedMaintenanceHandle> mismatch = capability.Consume(
            owner,
            closed.Generation,
            canonicalPath,
            actualMode,
            actualPurpose,
            lane);

        Result<IGrimoireTrackedMaintenanceHandle> spent = capability.Consume(
            owner,
            closed.Generation,
            canonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Assert.True(mismatch.IsFailure);

        Assert.True(spent.IsFailure);

    }

    private static async Task AssertCapabilityIdentityMismatchConsumesAsync(
        IGrimoireExclusiveClosedLease closed,
        IGrimoireMaintenanceIoLane lane,
        CovenantExclusiveRecoveryOwner actualOwner,
        long actualGeneration,
        string canonicalPath)
    {

        await using IGrimoireMaintenanceConnectionCapability capability =
            closed.IssueMaintenanceConnectionCapability(
                canonicalPath,
                CovenantMaintenanceConnectionMode.ReadOnly,
                CovenantMaintenanceConnectionPurpose.IntegrityVerification).Value;

        Result<IGrimoireTrackedMaintenanceHandle> mismatch = capability.Consume(
            actualOwner,
            actualGeneration,
            canonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Result<IGrimoireTrackedMaintenanceHandle> spent = capability.Consume(
            closed.Owner,
            closed.Generation,
            canonicalPath,
            CovenantMaintenanceConnectionMode.ReadOnly,
            CovenantMaintenanceConnectionPurpose.IntegrityVerification,
            lane);

        Assert.True(mismatch.IsFailure);

        Assert.True(spent.IsFailure);

    }

    private static IGrimoireClosingOwner Begin(
        GrimoireConnectionAdmissionGate gate,
        CovenantExclusiveRecoveryOwner owner,
        IGrimoireRequestLease? initiatingRequest = null,
        DbConnection? scopedConnection = null)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(
            owner,
            initiatingRequest,
            scopedConnection);

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

    private sealed class AsyncScopeDisposalSentinel(
        IAsyncDisposable inner,
        TaskCompletionSource databaseHolderDisposed,
        TaskCompletionSource allowScopeDisposal) : IAsyncDisposable
    {

        public async ValueTask DisposeAsync()
        {

            databaseHolderDisposed.TrySetResult();

            await allowScopeDisposal.Task;

            await inner.DisposeAsync();

        }

    }

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
