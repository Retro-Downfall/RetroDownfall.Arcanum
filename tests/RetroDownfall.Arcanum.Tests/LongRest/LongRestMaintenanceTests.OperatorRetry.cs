using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Operations;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// An operator's retry of a parked sweep is an explicit decision, and it used to wait out two timers: the reconciler's minute (it parks a retried row as
// long_rest.readmit_requested) and then the worker's (it scans for parked sweeps). The retry now settles the row at once and wakes the worker.
public sealed partial class LongRestMaintenanceTests
{
    private sealed class RecordingWake : ILongRestSweepWake
    {
        internal List<Guid> Woken { get; } = [];

        public void Wake(Guid operationId) => Woken.Add(operationId);
    }

    private static LongRunningOperationReconciler Reconciler(MaintenanceFixture f) => new(f.Operations, [new LongRestSweepRecoveryHandler()], TimeProvider.System,
        NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

    [Fact]
    public async Task An_operator_retry_of_a_parked_clean_sweep_parks_it_for_readmission_and_wakes_the_worker_without_waiting_for_a_timer()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(protectedSources: false);

        // The owner is gone: its lease lapses and recovery parks the sweep as interrupted.
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = Reconciler(f);

        _ = await reconciler.ReconcileNowAsync("recovery");

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(LongRestParkCodes.Interrupted, parked.TerminalErrorCode);

        // The operator retries it: the row is Pending, and until something reconciles it the worker cannot take it.
        Assert.True(await f.Operations.ResetForRetryAsync(f.Operation, parked.Revision, DateTimeOffset.UtcNow));

        LongRunningOperation retried = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Pending, retried.State);

        RecordingWake wake = new();

        await LongRunningOperationEndpoints.WakeAfterRetryAsync(retried, reconciler, wake, CancellationToken.None);

        LongRunningOperation settled = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, settled.State);

        Assert.Equal(LongRestParkCodes.ReadmitRequested, settled.TerminalErrorCode);

        Assert.Equal([f.Operation], wake.Woken);

        // And the worker readmits it as it is found: no cooldown, a fresh failure budget.
        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        LongRestSweepProgress progress = LongRestSweepHarness.Require(await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task An_operator_retry_of_another_kind_is_left_to_the_reconcilers_own_cadence_and_wakes_no_worker()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        LongRunningOperation other = await f.Operations.CreateAsync(new LongRunningOperationCreateRequest(LongRunningOperationKinds.BlobEncryptionMigration,
            LongRunningOperationRecoveryPolicy.RestartIdempotently, "Retry wake test.", now));

        RecordingWake wake = new();

        await LongRunningOperationEndpoints.WakeAfterRetryAsync(other, Reconciler(f), wake, CancellationToken.None);

        Assert.Empty(wake.Woken);

        Assert.Equal(LongRunningOperationState.Pending, (await f.Operations.GetAsync(other.Id))!.State);
    }
}
