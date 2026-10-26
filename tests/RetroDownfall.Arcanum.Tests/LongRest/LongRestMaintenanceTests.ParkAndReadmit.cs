using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData("readiness")]
    [InlineData("manual")]
    [InlineData("background")]
    public async Task Recovery_of_an_expired_clean_row_parks_it_with_no_provider_call_and_no_coordinator_construction(string entry)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperation before = (await f.Operations.GetAsync(f.Operation))!;

        // The registry exactly as the host registers it: one plain scoped handler. The coordinator is registered too, so
        // building it would be observable; resolving the registry must never construct it.
        int constructed = 0;

        ServiceCollection services = [];

        services.AddScoped<ILongRunningOperationRecoveryHandler, LongRestSweepRecoveryHandler>();

        services.AddScoped<LongRestSweepCoordinator>(_ =>
        {
            constructed++;

            return f.Coordinator;
        });

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        ILongRunningOperationRecoveryHandler[] handlers = [.. scope.ServiceProvider.GetServices<ILongRunningOperationRecoveryHandler>()];

        LongRunningOperationReconciliationSummary summary;

        if (entry == "background")
        {
            LongRunningOperationStartupHostedService recovery = new(new ParkingRecoveryScopes(f, () => (LongRestSweepRecoveryHandler)handlers.Single()),
                TimeProvider.System, new LongRunningOperationReconciliationStatus(), f.RecordedAdmission,
                NullLogger<LongRunningOperationStartupHostedService>.Instance);

            try
            {
                summary = await recovery.RunBackgroundPassAsync(DateTimeOffset.UtcNow, "background-recovery", CancellationToken.None);
            }
            finally
            {
                await recovery.StopAsync(CancellationToken.None);
            }
        }
        else
        {
            LongRunningOperationReconciler reconciler = new(f.Operations, handlers, TimeProvider.System,
                NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

            // Readiness reconciles through ReconcileNowAsync; the manual endpoint calls ReconcileAsync with its own bounds.
            summary = entry == "readiness"
                ? await reconciler.ReconcileNowAsync("readiness-recovery")
                : await reconciler.ReconcileAsync(DateTimeOffset.UtcNow, "manual-recovery", 500, 4, CancellationToken.None);
        }

        Assert.Equal(1, summary.RequiresAttention);

        Assert.Equal(0, summary.Completed);

        Assert.Equal(0, constructed);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Empty(f.Payloads);

        Assert.Equal(0, f.RecordedAdmission.EffectGroupAttempts);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(LongRestParkCodes.Interrupted, parked.TerminalErrorCode);

        Assert.Null(parked.LeaseOwner);

        Assert.Null(parked.LeaseExpiresAt);

        Assert.Equal(before.AttemptCount + 1, parked.AttemptCount);

        Assert.Equal(before.CheckpointPayload, parked.CheckpointPayload);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        Assert.Equal(DBNull.Value, await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task A_protected_row_parks_as_authorized_request_required_and_the_worker_is_never_offered_it()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: true);

        await ParkExpiredProtectedSweepAsync(f);

        Assert.Equal(0, f.Handler.SendCount);

        Assert.Null(LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, f.Clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        // Even the worker's own door refuses it: only a fresh capability readmits a protected park.
        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, progress.DeferredReason);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Theory]
    [InlineData("version-zero")]
    [InlineData("cancelling")]
    public async Task Version_zero_and_cancelling_rows_are_abandoned_by_recovery(string row)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: row == "cancelling", protectedSources: false);

        if (row == "version-zero")
        {
            // A lease taken before the first checkpoint: the sweep never began.
            DateTimeOffset now = DateTimeOffset.UtcNow;

            Assert.True((await f.Operations.TryAcquireLeaseAsync(f.Operation, "creation-gap-worker", now, now.AddMinutes(5))).Acquired);
        }
        else
        {
            LongRunningOperation running = (await f.Operations.GetAsync(f.Operation))!;

            Assert.True(await f.Operations.RequestCancellationAsync(f.Operation, running.Revision, DateTimeOffset.UtcNow));
        }

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("abandon-recovery");

        Assert.Equal(1, summary.Abandoned);

        LongRunningOperation terminal = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, terminal.State);

        Assert.Equal(row == "cancelling" ? LongRestParkCodes.Cancelled : LongRestParkCodes.NotStarted, terminal.TerminalErrorCode);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task The_worker_readmits_a_parked_clean_sweep_atomically_with_no_observable_Pending_and_finishes_it()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        await InstallStateLogAsync(f);

        // Readiness-style recovery parks the interrupted sweep, then the worker's real discovery and door finish it.
        RecoveredSweep recovered = await RecoverThenReadmitAsync(f, "atomic-readmission-recovery");

        Assert.Null(recovered.Fault);

        Assert.Equal(1, recovered.Parked.RequiresAttention);

        Assert.Equal(1, recovered.Completed);

        (long Old, long New)[] history = await ReadStateLogAsync(f);

        // The row went ReconciliationRequired -> Running exactly once, and no transition ever produced Pending (refused by
        // the trigger, so this also proves nothing tried): expiry discovery adopts a Pending row with a prior attempt.
        Assert.DoesNotContain(history, static step => step.New == (long)LongRunningOperationState.Pending);

        Assert.Single(history, static step => step.Old == (long)LongRunningOperationState.ReconciliationRequired && step.New == (long)LongRunningOperationState.Running);

        Assert.Equal(((long)LongRunningOperationState.Running, (long)LongRunningOperationState.Completed), history[^1]);

        Assert.Equal(1, f.Handler.SendCount > 0 ? 1 : 0);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        // Readmission restarts the park clock at the readmission instant; completion leaves it as a harmless record.
        Assert.NotEqual(DBNull.Value, await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task A_parked_protected_sweep_is_readmitted_atomically_by_a_fresh_capability_without_a_Pending_window()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        // No capability: the sweep begins, then parks for a fresh request.
        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, parked.DeferredReason);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);

        await InstallStateLogAsync(f);

        LongRestSweepProgress resumed = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, resumed.Phase);

        (long Old, long New)[] history = await ReadStateLogAsync(f);

        Assert.DoesNotContain(history, static step => step.New == (long)LongRunningOperationState.Pending);

        Assert.Single(history, static step => step.Old == (long)LongRunningOperationState.ReconciliationRequired && step.New == (long)LongRunningOperationState.Running);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task A_protected_park_without_a_capability_or_with_a_clean_code_is_not_readmitted()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        // The worker's door, with no capability, leaves a protected park alone.
        LongRestSweepProgress refused = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, refused.DeferredReason);

        LongRunningOperation after = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(parked with { CheckpointPayload = null }, after with { CheckpointPayload = null });

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task The_failure_budget_is_spent_by_four_failed_readmissions_and_the_fourth_park_needs_an_operator()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(clock: clock, protectedSources: false);

        await ParkExpiredSweepAsync(f, "budget-recovery");

        FailingMaintenance failing = new(ErrorCodes.LongRest.Unavailable);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, failing);

        for (int readmission = 1; readmission <= LongRestFaultPolicy.MaxReadmissions; readmission++)
        {
            long attempts = (await f.Operations.GetAsync(f.Operation))!.AttemptCount;

            LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

            LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

            Assert.Equal(attempts + 1, parked.AttemptCount);

            Assert.Equal(readmission, failing.Calls);

            Assert.Equal((long)readmission, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

            Assert.NotEqual(DBNull.Value, await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));

            if (readmission < LongRestFaultPolicy.MaxReadmissions)
            {
                Assert.Equal(ErrorCodes.LongRest.Unavailable, parked.TerminalErrorCode);

                // The cooldown is honoured to the second: refused one second early, accepted exactly on time.
                clock.Advance(LongRestFaultPolicy.Cooldown(readmission) - TimeSpan.FromSeconds(1));

                LongRestSweepProgress early = LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

                Assert.Equal(ErrorCodes.LongRest.Unavailable, early.DeferredReason);

                Assert.Equal(parked.AttemptCount, (await f.Operations.GetAsync(f.Operation))!.AttemptCount);

                Assert.Equal(readmission, failing.Calls);

                clock.Advance(TimeSpan.FromSeconds(1));
            }
            else
            {
                // The last readmission failed too: the park is no longer resumable.
                Assert.Equal(LongRestParkCodes.ReadmitExhausted, parked.TerminalErrorCode);
            }
        }

        clock.Advance(TimeSpan.FromDays(1));

        LongRestRunnableSweep found = LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None));

        Assert.Null(found.OperationId);

        Assert.Null(found.NextDueAt);

        LongRestSweepProgress deferred = LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.Equal(LongRestParkCodes.ReadmitExhausted, deferred.DeferredReason);

        Assert.Equal(LongRestFaultPolicy.MaxReadmissions, failing.Calls);

        // A new request for the same scope is refused rather than silently queued behind a sweep nobody will run.
        Result<LongRunningOperationDto> request = await coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);
    }

    [Fact]
    public async Task Operator_retry_returns_an_exhausted_sweep_with_readmit_requested_and_a_fresh_budget()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(clock: clock, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        await ParkExpiredSweepAsync(f, "retry-recovery");

        await ParkWithSqlAsync(f, LongRestParkCodes.ReadmitExhausted, LongRestFaultPolicy.MaxReadmissions, clock.GetUtcNow());

        Assert.Null(LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        // arcanum operation retry: the generic reset, then the next recovery pass.
        LongRunningOperation exhausted = (await f.Operations.GetAsync(f.Operation))!;

        Assert.True(await f.Operations.ResetForRetryAsync(f.Operation, exhausted.Revision, clock.GetUtcNow()));

        LongRunningOperation retried = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Pending, retried.State);

        Assert.True(retried.AttemptCount > 0);

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("operator-retry-recovery");

        Assert.Equal(1, summary.RequiresAttention);

        LongRunningOperation reparked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, reparked.State);

        Assert.Equal(LongRestParkCodes.ReadmitRequested, reparked.TerminalErrorCode);

        // The stale count survives until the worker readmits, but the retry code is runnable at once and ignores it.
        Assert.Equal((long)LongRestFaultPolicy.MaxReadmissions, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        // The retried readmission fails once more: the budget it leaves behind is one failure, not five.
        FailingMaintenance failing = new(ErrorCodes.LongRest.Unavailable);

        LongRestSweepCoordinator coordinator = CleanReviewCoordinator(f, f.RecordedAdmission, failing);

        LongRestSweepHarness.Require(await ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

        Assert.Equal(1, failing.Calls);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        LongRunningOperation again = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(ErrorCodes.LongRest.Unavailable, again.TerminalErrorCode);

        // A real worker then carries it to completion.
        clock.Advance(LongRestFaultPolicy.ReadmitBaseCooldown);

        LongRestSweepProgress finished = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, finished.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task Committed_progress_resets_the_failure_budget()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = 3;");

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task A_maintenance_yield_parks_as_maintenance_deferred_spends_no_budget_and_is_readmitted_after_reopen()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        using CancellationTokenSource stopping = new();

        using LongRestSweepHostedService host = PaidOwnershipHost(f);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        IGrimoireClosingOwner? closing = null;

        int fired = 0;

        f.RecordedAdmission.BeforeEffectGroupAdmission = () =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 1) return;

            closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value;

            entered.TrySetResult();
        };

        Task<Exception?> run = Record.ExceptionAsync(() => host.RunOneAsync(f.Operation, stopping.Token));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(closing);

            Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            // Maintenance refused the paid unit: nothing was sent and the sweep is parked under the free yield code.
            Assert.Equal(0, f.Handler.SendCount);

            LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

            Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

            Assert.Equal(LongRestParkCodes.MaintenanceDeferred, parked.TerminalErrorCode);

            Assert.Null(parked.LeaseOwner);

            Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

            Assert.False(run.IsCompleted);

            // Reopen: the worker's retry loop readmits the same parked row and finishes the sweep without waiting out a cooldown.
            Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

            Assert.Null(await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            f.RecordedAdmission.BeforeEffectGroupAdmission = static () => { };

            await stopping.CancelAsync();

            if (closing is not null) await closing.DisposeAsync();
        }

        LongRunningOperation final = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Completed, final.State);

        Assert.True(f.Handler.SendCount > 0);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task A_deferral_before_the_first_checkpoint_keeps_the_lease_for_recovery_to_abandon_as_never_started()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        IGrimoireClosingOwner? closing = null;

        // Maintenance closes admission the instant the coordinator wins its lease, before the sweep saves anything.
        HookedLongRunningOperationStore operations = new(f.Operations)
        {
            AfterLeaseAcquired = () => closing = f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()).Value,
        };

        LongRestSweepCoordinator coordinator = new(operations, new LongRestSweepStore(f.H.Saga.Context, f.Clock), f.H.Store, f.Epoch,
            CleanReviewFacts(f), f.Maintenance, f.Gate, new LongRunningOperationOwnership(), new LongRestSweepQueue(),
            f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, f.RecordedAdmission, NullLogger<LongRestSweepCoordinator>.Instance);

        try
        {
            Exception? deferred = await Record.ExceptionAsync(() => ResumeAdmittedWithAsync(coordinator, f.RecordedAdmission, f.Operation, CancellationToken.None));

            Assert.IsType<LongRestMaintenanceDeferredException>(deferred);

            // Nothing was saved, so there is nothing to park: the worker's lease stays and its expiry is the debt.
            LongRunningOperation held = (await f.Operations.GetAsync(f.Operation))!;

            Assert.Equal(LongRunningOperationState.Running, held.State);

            Assert.Equal(0, held.CheckpointVersion);

            Assert.NotNull(held.LeaseOwner);

            Assert.Null(held.TerminalErrorCode);
        }
        finally
        {
            if (closing is not null) await closing.DisposeAsync();
        }

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], f.Clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        Assert.Equal(1, (await reconciler.ReconcileNowAsync("never-started-recovery")).Abandoned);

        Assert.Equal(LongRestParkCodes.NotStarted, (await f.Operations.GetAsync(f.Operation))!.TerminalErrorCode);
    }

    [Fact]
    public async Task A_parked_clean_sweep_is_returned_unchanged_by_a_request_with_zero_writes_and_wakes_the_worker()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await ParkExpiredSweepAsync(f, "parked-is-active-recovery");

        LongRestSweepQueue queue = new();

        LongRestSweepCoordinator coordinator = ParkedActiveCoordinator(f, queue);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        LongRunningOperationDto requested = LongRestSweepHarness.Require(await coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None));

        Assert.Equal(f.Operation, requested.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, requested.State);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);

        // The accepted request wakes the worker so a parked clean sweep does not wait for its next timer.
        Assert.Equal(f.Operation, await queue.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_parked_sweep_from_another_boot_is_still_active_but_a_changed_configuration_value_retires_and_recreates_it()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await ParkExpiredSweepAsync(f, "boot-recovery");

        Guid parkedId = f.Operation;

        await f.RebootAsync();

        string before = await ParkedSnapshotAsync(f);

        LongRunningOperationDto sameBoot = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None));

        Assert.Equal(parkedId, sameBoot.Id);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        // Now the configuration value itself changes: the parked sweep can never resume, so it retires and a new one is created.
        ArcanumSettings changed = LongRestConfigurationEpochTests.Settings();

        changed.Cost.Pricing.DefaultPricing!.InputPer1M = 11m;

        f.Options.Set(changed);

        LongRunningOperationDto replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None));

        Assert.NotEqual(parkedId, replacement.Id);

        Assert.Equal(LongRunningOperationState.Pending, replacement.State);

        LongRunningOperation retired = (await f.Operations.GetAsync(parkedId))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(LongRestParkCodes.StaleConfiguration, retired.TerminalErrorCode);

        Assert.NotNull(retired.CompletedAt);

        Assert.Null(retired.LeaseOwner);

        Assert.Equal((long)LongRestSweepPhase.Retired, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{parkedId:N}';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_sweeps WHERE PhaseCode NOT IN (6, 7, 9);"));

        // The retired generation keeps its evidence for the paid history, but resumes nothing.
        Assert.Equal(parkedId, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(parkedId, CancellationToken.None))!.OperationId);
    }

    [Theory]
    [InlineData("LongRest.IntegrityFailure", 0)]
    [InlineData("LongRest.StaleInput", 0)]
    [InlineData("long_rest.readmit_exhausted", 4)]
    [InlineData("long_rest.interrupted", 4)]
    public async Task A_park_no_automatic_path_may_resume_is_refused_as_SweepParked_with_zero_writes(string code, int readmitCount)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        await ParkWithSqlAsync(f, code, readmitCount, DateTimeOffset.UtcNow);

        string before = await ParkedSnapshotAsync(f);

        long changes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> requested = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(requested.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, requested.Error.Code);

        // The refusal names the parked operation (a content-free identifier) so the remedy can be run against it.
        Assert.Contains(f.Operation.ToString("D"), requested.Error.Message, StringComparison.Ordinal);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        Assert.Equal(changes, (long)(await f.H.ScalarAsync("SELECT total_changes();"))!);
    }

    [Fact]
    public async Task A_parked_protected_sweep_is_returned_unchanged_across_a_boot_and_retired_when_the_value_changes()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Guid parkedId = f.Operation;

        Assert.Equal(parkedId, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None)).Id);

        await f.RebootAsync();

        string before = await ParkedSnapshotAsync(f);

        Assert.Equal(parkedId, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None)).Id);

        Assert.Equal(before, await ParkedSnapshotAsync(f));

        ArcanumSettings changed = LongRestConfigurationEpochTests.Settings();

        changed.Cost.Pricing.DefaultPricing!.InputPer1M = 13m;

        f.Options.Set(changed);

        LongRunningOperationDto replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None));

        Assert.NotEqual(parkedId, replacement.Id);

        Assert.Equal(LongRunningOperationState.Abandoned, (await f.Operations.GetAsync(parkedId))!.State);
    }

    /// <summary>Everything a request must not touch on a parked sweep, as one comparable text.</summary>
    private static async Task<string> ParkedSnapshotAsync(MaintenanceFixture f) => string.Join('\n',
        await f.H.ScalarAsync("SELECT json_group_array(json_array(Id, State, Revision, AttemptCount, LeaseOwner, LeaseExpiresAt, TerminalErrorCode, hex(CheckpointPayload))) FROM LongRunningOperations;"),
        await f.H.ScalarAsync("SELECT json_group_array(json_array(OperationId, GenerationId, PhaseCode, Revision, ConfigurationJson, ReadmitCount, ParkedAt, UpdatedAtUtc)) FROM long_rest_sweeps;"));

    private static LongRestSweepCoordinator ParkedActiveCoordinator(MaintenanceFixture f, LongRestSweepQueue queue) =>
        new(f.Operations, new LongRestSweepStore(f.H.Saga.Context, f.Clock), f.H.Store, f.Epoch, CleanReviewFacts(f), f.Maintenance, f.Gate,
            new LongRunningOperationOwnership(), queue, f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, f.RecordedAdmission, NullLogger<LongRestSweepCoordinator>.Instance);

    /// <summary>A protected sweep: park it through the real reconciler with the pure handler (the sweep must have begun).</summary>
    private static async Task ParkExpiredProtectedSweepAsync(MaintenanceFixture f)
    {
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], f.Clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileNowAsync("protected-recovery");

        Assert.Equal(1, summary.RequiresAttention);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, parked.TerminalErrorCode);
    }

    /// <summary>Parks the fixture's real started sweep exactly as the pure handler or the coordinator would leave it.</summary>
    internal static async Task ParkWithSqlAsync(MaintenanceFixture f, string code, int readmitCount, DateTimeOffset? parkedAt)
    {
        string id = f.Operation.ToString("N");

        await f.H.ExecuteAsync($"UPDATE LongRunningOperations SET State = {(int)LongRunningOperationState.ReconciliationRequired}, LeaseOwner = NULL, "
            + "LeaseExpiresAt = NULL, TerminalErrorCode = $code, CheckpointVersion = 1, "
            + "CheckpointPayload = COALESCE(CheckpointPayload, X'00'), Revision = Revision + 1 WHERE Id = $id;", ("$code", code), ("$id", id));

        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = $count, ParkedAt = $at WHERE OperationId = $id;",
            ("$count", readmitCount), ("$at", parkedAt is { } at ? UtcInstantText.Format(at) : null), ("$id", id));
    }

    /// <summary>
    /// Logs every state write to the fixture sweep's operation row and refuses any write to Pending. A Pending row with a
    /// prior attempt is exactly what the reconciler's expiry discovery adopts, so a readmission may never pass through it.
    /// </summary>
    private static async Task InstallStateLogAsync(MaintenanceFixture f)
    {
        string id = f.Operation.ToString("N");

        await f.H.ExecuteAsync("CREATE TEMP TABLE IF NOT EXISTS state_log (Seq INTEGER PRIMARY KEY AUTOINCREMENT, OldState INTEGER, NewState INTEGER);");

        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER IF NOT EXISTS state_log_write AFTER UPDATE OF State ON LongRunningOperations WHEN NEW.Id = '{id}' "
            + "BEGIN INSERT INTO state_log (OldState, NewState) VALUES (OLD.State, NEW.State); END;");

        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER IF NOT EXISTS state_log_refuse_pending BEFORE UPDATE OF State ON LongRunningOperations WHEN NEW.Id = '{id}' "
            + $"AND NEW.State = {(int)LongRunningOperationState.Pending} BEGIN SELECT RAISE(ABORT, 'the sweep may not pass through Pending'); END;");
    }

    private static async Task<(long Old, long New)[]> ReadStateLogAsync(MaintenanceFixture f)
    {
        List<(long, long)> rows = [];

        await using Microsoft.Data.Sqlite.SqliteCommand command = f.H.Connection.CreateCommand();

        command.CommandText = "SELECT OldState, NewState FROM state_log ORDER BY Seq;";

        await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) rows.Add((reader.GetInt64(0), reader.GetInt64(1)));

        return [.. rows];
    }

    /// <summary>A maintenance unit that fails the sweep with a chosen code before any progress is made.</summary>
    private sealed class FailingMaintenance(string code) : ILongRestAdmittedMaintenance
    {
        internal int Calls;

        public Task<Result<LongRestSweepProgress>> ProcessAdmittedAsync(Guid operationId, string workerOwner,
            LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, CancellationToken ct)
        {
            Calls++;

            return Task.FromResult(Result<LongRestSweepProgress>.Failure(new Error(code, "Fixture failure before any progress.")));
        }
    }

    /// <summary>
    /// What recovery of an expired sweep produces now that recovery only parks: the pure handler's park, then whatever the
    /// hosted worker's real discovery and door did with the parked row.
    /// </summary>
    /// <param name="Parked">The recovery pass summary, taken before any readmission.</param>
    /// <param name="Readmission">The worker door's result, or null when discovery offered the worker nothing.</param>
    /// <param name="Fault">The exception the worker door raised, if any.</param>
    /// <param name="State">The durable operation state once the worker door returned.</param>
    internal sealed record RecoveredSweep(LongRunningOperationReconciliationSummary Parked, Result<LongRestSweepProgress>? Readmission,
        Exception? Fault, LongRunningOperationState State)
    {
        /// <summary>1 when the worker carried the parked sweep all the way to Completed.</summary>
        internal int Completed => State == LongRunningOperationState.Completed ? 1 : 0;

        /// <summary>1 when the sweep is parked (or left parked) for the next authority to look at.</summary>
        internal int RequiresAttention => State == LongRunningOperationState.ReconciliationRequired ? 1 : 0;
    }

    /// <summary>
    /// Lapses the fixture sweep's lease the way elapsed time would. Recovery adopts a row only after its lease has lapsed,
    /// which is five minutes after the last renewal, so a readmission stamp the lease outlived is that much older too.
    /// </summary>
    internal static async Task LapseLeaseAsync(MaintenanceFixture f)
    {
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ParkedAt = $at WHERE OperationId = $operation AND ParkedAt IS NOT NULL;",
            ("$at", UtcInstantText.Format(f.Clock.GetUtcNow() - TimeSpan.FromMinutes(5))), ("$operation", f.Operation.ToString("N")));
    }

    /// <summary>
    /// Expires the sweep's lease and runs the registered recovery pass, which only parks it; then drives the hosted worker's
    /// real discovery and its admitted door against whatever it finds, exactly as the worker's loop does.
    /// </summary>
    internal static async Task<RecoveredSweep> RecoverThenReadmitAsync(MaintenanceFixture f, string recoveryOwner,
        LongRestSweepCoordinator? coordinator = null, IGrimoireConnectionAdmissionGate? gate = null)
    {
        await LapseLeaseAsync(f);

        LongRunningOperationStartupHostedService recovery = new(new ParkingRecoveryScopes(f), TimeProvider.System,
            new LongRunningOperationReconciliationStatus(), gate ?? f.RecordedAdmission,
            NullLogger<LongRunningOperationStartupHostedService>.Instance);

        LongRunningOperationReconciliationSummary parked;

        try
        {
            parked = await recovery.RunBackgroundPassAsync(DateTimeOffset.UtcNow, recoveryOwner, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }

        Result<LongRestSweepProgress>? readmission = null;

        Exception? fault = null;

        LongRestRunnableSweep found = LongRestSweepHarness.Require(await new LongRestSweepStore(f.H.Saga.Context, f.Clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None));

        if (found.OperationId is { } operation)
        {
            fault = await Record.ExceptionAsync(async () => readmission = await ResumeAdmittedWithAsync(
                coordinator ?? f.Coordinator, gate ?? f.RecordedAdmission, operation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        }

        return new(parked, readmission, fault, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    /// <summary>The worker loop's door: a fresh Long Rest sweep lease from <paramref name="gate"/>, passed by value.</summary>
    internal static async Task<Result<LongRestSweepProgress>> ResumeAdmittedWithAsync(LongRestSweepCoordinator coordinator,
        IGrimoireConnectionAdmissionGate gate, Guid operation, CancellationToken ct)
    {
        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted))
        {
            throw new LongRestMaintenanceDeferredException(CancellationToken.None);
        }

        await using IGrimoireWorkLease lease = admitted!;

        return await coordinator.ResumeAdmittedAsync(operation, lease, ct);
    }

    // Only scope routing is adapted to the existing native fixture. Discovery, classified lease acquisition, the
    // reconciler, the registered handler and the gate are real.
    internal sealed class ParkingRecoveryScopes(MaintenanceFixture fixture, Func<LongRestSweepRecoveryHandler>? handler = null) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new ParkingRecoveryScope(fixture, handler,
            fixture.Services.GetRequiredService<IServiceScopeFactory>().CreateScope());
    }

    private sealed class ParkingRecoveryScope(MaintenanceFixture fixture, Func<LongRestSweepRecoveryHandler>? handler, IServiceScope inner)
        : IServiceScope, IServiceProvider, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ILongRunningOperationGenericRecoveryDiscovery)) return fixture.Operations;

            if (serviceType == typeof(LongRunningOperationReconciler))
            {
                return new LongRunningOperationReconciler(fixture.Operations, [handler?.Invoke() ?? new LongRestSweepRecoveryHandler()],
                    TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());
            }

            return inner.ServiceProvider.GetService(serviceType);
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner is IAsyncDisposable asynchronous ? asynchronous.DisposeAsync() : DisposeSynchronously();

        private ValueTask DisposeSynchronously()
        {
            inner.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
