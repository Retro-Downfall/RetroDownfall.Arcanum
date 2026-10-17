using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Clean readmission and completed-checkpoint reconciliation retry SQLITE_BUSY/LOCKED around a whole transaction that attests the sweep lease
// again and again. The recording gate raises a busy answer in the middle of one attempt, which rolls that attempt back; the next attempt must
// start over, attest the same lease from its first validation, and commit what the first never did. A busy answer that outlasts the bound must
// surface as the busy timeout and leave the store exactly as it was.
public sealed partial class LongRestMaintenanceTests
{
    // ReadmitCleanAsync: three validations of two attestations each, after the coordinator pre-check.
    private const int ReadmissionStoreAttestations = 6;

    // ReconcileCompletedCheckpointAsync: three proofs of two validations of two attestations each, after the coordinator pre-check.
    private const int CompletedCheckpointStoreAttestations = 12;

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public async Task Clean_readmission_retries_a_busy_attempt_and_attests_the_same_lease_from_its_first_validation(int busyAt)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        await RetainPaidSummaryAcrossClosingAsync(f, drained: true);

        Assert.True(f.Admission.CurrentGeneration > original.Fence.AdmissionGeneration);

        Assert.Equal(1, f.Handler.SendCount);

        RecoveredSweep recovered = await RecoverThroughOneBusyAttemptAsync(f, original.Fence.AdmissionGeneration, busyAt,
            ReadmissionStoreAttestations, "clean-readmission-busy");

        // The adoption the first attempt rolled back was committed by the second, and nothing was sent twice.
        LongRestDiscoveryGeneration current = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(original.Fence with { AdmissionGeneration = f.Admission.CurrentGeneration }, current.Fence);

        Assert.Equal(LongRestSweepPhase.Completed, current.Phase);

        Assert.Equal((long)f.Handler.SendCount, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task Clean_readmission_surfaces_the_busy_timeout_and_changes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        await RetainPaidSummaryAcrossClosingAsync(f, drained: true);

        CleanStoreSnapshot before = await CaptureCleanStoreAsync(f);

        // The third ask is the store's second attestation, after the adoption's first checks have run inside the transaction.
        GrimoireBusyTimeoutException timeout = await RecoverUntilTheBusyBoundAsync(f, busyFrom: 3, "clean-readmission-timeout");

        Assert.Equal(1, timeout.Attempts);

        await AssertCleanStoreUnchangedAsync(f, before);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(13)]
    public async Task Clean_completed_checkpoint_retries_a_busy_attempt_and_attests_the_same_lease_from_its_first_validation(int busyAt)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, exact: true);

        LongRestDiscoveryGeneration original = await InterruptAfterTheGenerationCompletedAsync(f);

        RecoveredSweep recovered = await RecoverThroughOneBusyAttemptAsync(f, original.Fence.AdmissionGeneration, busyAt,
            CompletedCheckpointStoreAttestations, "clean-checkpoint-busy");

        // The catch-up write sits between the second and third proof. A busy answer after it rolled it back, and the second attempt wrote it once.
        LongRunningOperation final = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Completed, final.State);

        LongRestSweepCheckpoint caughtUp = LongRestSweepHarness.Require(
            LongRestSweepCheckpointCodec.Decode(f.Operation, final.CheckpointPayload!));

        Assert.Equal(LongRestSweepPhase.Completed, caughtUp.Phase);

        Assert.Equal((long)(await f.H.ScalarAsync("SELECT Revision FROM long_rest_sweeps;"))!, caughtUp.Revision);

        Assert.Equal(2, f.Handler.SendCount);
    }

    [Fact]
    public async Task Clean_completed_checkpoint_surfaces_the_busy_timeout_and_changes_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, exact: true);

        await InterruptAfterTheGenerationCompletedAsync(f);

        CleanStoreSnapshot before = await CaptureCleanStoreAsync(f);

        // The tenth ask is the first of the third proof, after the catch-up write: the rollback has something to undo.
        GrimoireBusyTimeoutException timeout = await RecoverUntilTheBusyBoundAsync(f, busyFrom: 10, "clean-checkpoint-timeout");

        Assert.Equal(1, timeout.Attempts);

        await AssertCleanStoreUnchangedAsync(f, before);

        // The ledger still lags the completed generation: the write the timed-out attempt made was rolled back with it.
        LongRestSweepCheckpoint stored = LongRestSweepHarness.Require(
            LongRestSweepCheckpointCodec.Decode(f.Operation, (await f.Operations.GetAsync(f.Operation))!.CheckpointPayload!));

        Assert.NotEqual(LongRestSweepPhase.Completed, stored.Phase);
    }

    /// <summary>
    /// Recovers the parked sweep with exactly one busy answer: the <paramref name="busyAt"/>th attestation of the gate (the first is the
    /// coordinator pre-check, which attests) is interrupted the way a locked database interrupts a store statement. Every attestation in
    /// the window that covers the pre-check, the first attempt up to the busy answer and the whole second attempt presents the one sweep lease
    /// the worker's door minted, and each of them but the interrupted one attests the interval from the original admission.
    /// </summary>
    private static async Task<RecoveredSweep> RecoverThroughOneBusyAttemptAsync(MaintenanceFixture f, long previousAdmission, int busyAt,
        int storeAttestations, string recoveryOwner)
    {
        Assert.InRange(busyAt, 2, 1 + storeAttestations);

        int attestationsBefore = f.RecordedAdmission.DrainedAttestations.Count;

        int presentedBefore = f.RecordedAdmission.PresentedLeases.Count;

        int leasesBefore = f.RecordedAdmission.InnerWorkLeases.Count;

        int busyAnswers = 0;

        f.RecordedAdmission.BeforeDrainedAttestation = ordinal =>
        {
            if (ordinal != attestationsBefore + busyAt || Interlocked.Increment(ref busyAnswers) != 1) return true;

            throw new SqliteException("busy", 5);
        };

        RecoveredSweep recovered;

        try
        {
            recovered = await RecoverThenReadmitAsync(f, recoveryOwner);
        }
        finally
        {
            f.RecordedAdmission.BeforeDrainedAttestation = static _ => true;
        }

        Assert.Equal(1, busyAnswers);

        Assert.Null(recovered.Fault);

        Assert.Equal(1, recovered.Completed);

        // The retry runs under the lease the door already holds: no second Long Rest lease was minted for the second attempt.
        Assert.Single(f.RecordedAdmission.InnerWorkLeases.Skip(leasesBefore), static lease => lease.Kind == GrimoireWorkKind.LongRestSweep);

        int window = busyAt + storeAttestations;

        IGrimoireWorkLease[] presented = [.. f.RecordedAdmission.PresentedLeases.Skip(presentedBefore)];

        Assert.True(presented.Length >= window, $"expected at least {window} attestations to be asked for, saw {presented.Length}");

        IGrimoireWorkLease sweepLease = presented[0];

        Assert.Equal(GrimoireWorkKind.LongRestSweep, sweepLease.Kind);

        Assert.Contains(sweepLease, f.RecordedAdmission.IssuedWorkLeases);

        Assert.All(presented.Take(window), lease => Assert.Same(sweepLease, lease));

        RecordingGrimoireWorkAdmissionGate.DrainedAttestation[] attested = [.. f.RecordedAdmission.DrainedAttestations.Skip(attestationsBefore)];

        // The interrupted ask never reached a verdict, so it has no record; the rest of the window is the pre-check, the attempt it
        // interrupted and every validation of the attempt that followed.
        Assert.True(attested.Length >= window - 1, $"expected at least {window - 1} attestations, saw {attested.Length}");

        Assert.All(attested.Take(window - 1), attestation =>
        {
            Assert.True(attestation.Attested);

            Assert.Equal(GrimoireWorkKind.LongRestSweep, attestation.Kind);

            Assert.Equal(previousAdmission, attestation.Previous);

            Assert.Equal(f.Admission.CurrentGeneration, attestation.Current);
        });

        return recovered;
    }

    /// <summary>
    /// Recovers the parked sweep with the busy bound already spent and every attestation from the <paramref name="busyFrom"/>th on busy, so the
    /// first busy answer the store sees is also the last: it must reach the caller as the busy timeout.
    /// </summary>
    private static async Task<GrimoireBusyTimeoutException> RecoverUntilTheBusyBoundAsync(MaintenanceFixture f, int busyFrom, string recoveryOwner)
    {
        int attestationsBefore = f.RecordedAdmission.DrainedAttestations.Count;

        f.Payloads.Clear();

        f.H.Store.BusyRetryDeadlineForTesting = TimeSpan.Zero;

        f.RecordedAdmission.BeforeDrainedAttestation = ordinal => ordinal < attestationsBefore + busyFrom
            ? true
            : throw new SqliteException("busy", 5);

        RecoveredSweep recovered;

        try
        {
            recovered = await RecoverThenReadmitAsync(f, recoveryOwner);
        }
        finally
        {
            f.RecordedAdmission.BeforeDrainedAttestation = static _ => true;

            f.H.Store.BusyRetryDeadlineForTesting = null;
        }

        GrimoireBusyTimeoutException timeout = Assert.IsType<GrimoireBusyTimeoutException>(recovered.Fault);

        Assert.Equal(TimeSpan.Zero, timeout.Deadline);

        Assert.Equal(5, Assert.IsType<SqliteException>(timeout.InnerException).SqliteErrorCode);

        Assert.Null(recovered.Readmission);

        return timeout;
    }

    private static async Task AssertCleanStoreUnchangedAsync(MaintenanceFixture f, CleanStoreSnapshot before)
    {
        CleanStoreSnapshot after = await CaptureCleanStoreAsync(f);

        Assert.Equal(before.Generation, after.Generation);

        Assert.Equal(before.Units, after.Units);

        Assert.Equal(before.Immutable, after.Immutable);

        Assert.Equal(before.Checkpoint, after.Checkpoint);

        Assert.Equal(before.Current, after.Current);

        Assert.Equal(before.SendCount, f.Handler.SendCount);

        Assert.Empty(f.Payloads);
    }

    /// <summary>
    /// The window the completed-checkpoint store method exists for: the generation completed, the ledger's checkpoint write was interrupted,
    /// and the admission has since drained and reopened. Returns the completed generation as it was stored.
    /// </summary>
    private static async Task<LongRestDiscoveryGeneration> InterruptAfterTheGenerationCompletedAsync(MaintenanceFixture f)
    {
        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fixture_busy_retry_crash BEFORE UPDATE OF CheckpointPayload ON LongRunningOperations WHEN (SELECT PhaseCode FROM long_rest_discovery_generations) = 6 BEGIN SELECT RAISE(ABORT, 'fixture busy retry crash'); END;");

        try
        {
            SqliteException crash = await Assert.ThrowsAsync<SqliteException>(() =>
                f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

            Assert.Equal(19, crash.SqliteErrorCode);
        }
        finally
        {
            await f.H.ExecuteAsync("DROP TRIGGER fixture_busy_retry_crash;");
        }

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(LongRestSweepPhase.Completed, original.Phase);

        Assert.Equal(2, f.Handler.SendCount);

        await FinishDrainedEdgeAsync(f);

        f.Payloads.Clear();

        return original;
    }
}
