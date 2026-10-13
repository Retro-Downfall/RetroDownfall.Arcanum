using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The coordinator pre-check and each store validation consult the gate independently. These tests let the pre-check
// attest and then make ONE later store attestation refuse, through the recording gate's refuse-only hook or through a
// real BeginOrResumeExclusive on the inner gate. A store that ignored its own verdict (while still calling the gate)
// would commit the adoption or the checkpoint write below and turn every row red.
public sealed partial class LongRestMaintenanceTests
{
    // ReadmitCleanAsync: pre-check (1), then three ValidateCleanOwnerAsync calls of two attestations each (2-7).
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(2, true)]
    [InlineData(7, true)]
    public async Task Clean_readmission_store_attestation_verdict_gates_the_adoption(int validation, bool maintenanceBegins)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        await RetainPaidSummaryAcrossClosingAsync(f, drained: true);

        Assert.True(f.Admission.CurrentGeneration > original.Fence.AdmissionGeneration);

        Assert.Equal(1, f.Handler.SendCount);

        CleanStoreSnapshot before = await CaptureCleanStoreAsync(f);

        await RefuseOneCleanStoreAttestationAsync(f, original.Fence.AdmissionGeneration, validation, maintenanceBegins, before);
    }

    // ReconcileCompletedCheckpointAsync: pre-check (1), then three ValidateCompletedCheckpointProofAsync calls of two
    // ValidateCleanOwnerAsync calls each (2-5, 6-9, 10-13). The checkpoint UPDATE runs between the second and third proof.
    [Theory]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(13, false)]
    [InlineData(2, true)]
    [InlineData(13, true)]
    public async Task Clean_completed_checkpoint_store_attestation_verdict_gates_the_ledger_write(int validation, bool maintenanceBegins)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, exact: true);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        // The real coordinator checkpoint bytes are interrupted only after the generation completed.
        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fixture_store_attestation_crash BEFORE UPDATE OF CheckpointPayload ON LongRunningOperations WHEN (SELECT PhaseCode FROM long_rest_discovery_generations) = 6 BEGIN SELECT RAISE(ABORT, 'fixture store attestation crash'); END;");

        try
        {
            SqliteException crash = await Assert.ThrowsAsync<SqliteException>(() =>
                f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

            Assert.Equal(19, crash.SqliteErrorCode);
        }
        finally
        {
            await f.H.ExecuteAsync("DROP TRIGGER fixture_store_attestation_crash;");
        }

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(LongRestSweepPhase.Completed, original.Phase);

        LongRunningOperation interrupted = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Running, interrupted.State);

        LongRestSweepCheckpoint lagging = LongRestSweepHarness.Require(
            LongRestSweepCheckpointCodec.Decode(f.Operation, interrupted.CheckpointPayload!));

        Assert.NotEqual(LongRestSweepPhase.Completed, lagging.Phase);

        Assert.Equal(2, f.Handler.SendCount);

        await FinishDrainedEdgeAsync(f);

        CleanStoreSnapshot before = await CaptureCleanStoreAsync(f);

        Assert.Equal(interrupted.CheckpointPayload, before.Checkpoint);

        await RefuseOneCleanStoreAttestationAsync(f, original.Fence.AdmissionGeneration, validation, maintenanceBegins, before);

        // The catch-up write is the only thing the facet may do, and it must not have survived.
        LongRestSweepCheckpoint stored = LongRestSweepHarness.Require(
            LongRestSweepCheckpointCodec.Decode(f.Operation, (await f.Operations.GetAsync(f.Operation))!.CheckpointPayload!));

        Assert.NotEqual(LongRestSweepPhase.Completed, stored.Phase);
    }

    private static async Task RefuseOneCleanStoreAttestationAsync(MaintenanceFixture f, long originalPrevious, int validation,
        bool maintenanceBegins, CleanStoreSnapshot before)
    {
        // Attestation 1 is the coordinator pre-check, which must attest: only a later STORE attestation is refused.
        Assert.True(validation >= 2);

        int baseline = f.RecordedAdmission.DrainedAttestations.Count;

        long admitted = f.Admission.CurrentGeneration;

        IGrimoireClosingOwner? closing = null;

        f.RecordedAdmission.BeforeDrainedAttestation = ordinal =>
        {
            if (ordinal != baseline + validation) return true;

            if (!maintenanceBegins) return false;

            // A real gate edge: maintenance begins between the attestations. The inner gate decides the verdict.
            closing = LongRestSweepHarness.Require(f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));

            return true;
        };

        try
        {
            (LongRunningOperationReconciliationSummary? summary, Exception? stopped) = await RunCleanStorePassAsync(f);

            // Nothing the refused validation guarded may have survived: the transaction rolled back whole.
            CleanStoreSnapshot after = await CaptureCleanStoreAsync(f);

            Assert.Equal(before.Generation, after.Generation);

            Assert.Equal(before.Units, after.Units);

            Assert.Equal(before.Immutable, after.Immutable);

            Assert.Equal(before.Checkpoint, after.Checkpoint);

            Assert.Equal(before.Current, after.Current);

            Assert.Equal(before.SendCount, f.Handler.SendCount);

            Assert.Empty(f.Payloads);

            RecordingGrimoireWorkAdmissionGate.DrainedAttestation[] attested = f.RecordedAdmission.DrainedAttestations.Skip(baseline).ToArray();

            // The pre-check and every earlier store attestation held; the refused one is the last: nothing ran after it.
            Assert.Equal(validation, attested.Length);

            Assert.All(attested.Take(validation - 1), attestation => Assert.True(attestation.Attested));

            Assert.False(attested[^1].Attested);

            Assert.All(attested, attestation =>
            {
                Assert.Equal(GrimoireWorkKind.LongRunningOperationRecovery, attestation.Kind);

                Assert.Equal(originalPrevious, attestation.Previous);

                Assert.Equal(admitted, attestation.Current);
            });

            if (maintenanceBegins)
            {
                Assert.NotNull(closing);

                // Maintenance classifies from the lease's own revocation obligation: deferred, never a terminal refusal.
                Assert.IsType<LongRestMaintenanceDeferredException>(stopped);

                Assert.Null(summary);

                Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);
            }
            else
            {
                Assert.Null(stopped);

                Assert.NotNull(summary);

                Assert.Equal(0, summary.Completed);

                Assert.Equal(1, summary.RequiresAttention);

                Assert.NotEqual(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
            }
        }
        finally
        {
            f.RecordedAdmission.BeforeDrainedAttestation = static _ => true;

            if (closing is not null)
            {
                Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

                Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

                await closing.DisposeAsync();
            }
        }
    }

    // Only the durable lease deadline is expired; the same registered handler, coordinator, gate, stores and worker run.
    private static async Task<(LongRunningOperationReconciliationSummary? Summary, Exception? Stopped)> RunCleanStorePassAsync(MaintenanceFixture f)
    {
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation;",
            ("$operation", f.Operation.ToString("N")));

        f.Payloads.Clear();

        LongRunningOperationStartupHostedService recovery = new(new PaidRecoveryScopes(f), TimeProvider.System,
            new LongRunningOperationReconciliationStatus(), f.RecordedAdmission,
            NullLogger<LongRunningOperationStartupHostedService>.Instance);

        LongRunningOperationReconciliationSummary? summary = null;

        try
        {
            Exception? stopped = await Record.ExceptionAsync(async () => summary = await recovery.RunBackgroundPassAsync(
                DateTimeOffset.UtcNow, "clean-store-attestation", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

            return (summary, stopped);
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }
    }

    private static async Task FinishDrainedEdgeAsync(MaintenanceFixture f)
    {
        long admissionBefore = f.Admission.CurrentGeneration;

        IGrimoireClosingOwner closing = LongRestSweepHarness.Require(f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));

        try
        {
            Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);
        }
        finally
        {
            await closing.DisposeAsync();
        }

        Assert.Equal(admissionBefore + 1, f.Admission.CurrentGeneration);
    }

    private static async Task<CleanStoreSnapshot> CaptureCleanStoreAsync(MaintenanceFixture f) => new(
        await f.H.ScalarAsync(CleanGenerationSnapshotSql),
        await f.H.ScalarAsync(CleanUnitSnapshotSql),
        await CleanReviewImmutableAsync(f),
        (await f.Operations.GetAsync(f.Operation))!.CheckpointPayload,
        LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None)),
        f.Handler.SendCount);

    private sealed record CleanStoreSnapshot(object? Generation, object? Units, string Immutable, byte[]? Checkpoint,
        LongRestDiscoveryGeneration? Current, int SendCount);
}
