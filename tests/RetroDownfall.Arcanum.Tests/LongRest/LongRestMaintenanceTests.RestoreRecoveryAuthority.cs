using Microsoft.Data.Sqlite;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Backup;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Authenticated_committed_restore_recovery_keeps_unsanitized_paid_generation_closed(int proofKind)
    {
        await using MaintenanceFixture source = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        _ = await AddLinkedEmptyRestoreGenerationAsync(source);

        await using MemoryErasureRestoreHarness archived = await MemoryErasureRestoreHarness.CreateAsync();

        await SnapshotPaidRestoreInstallationAsync(source, archived);

        string archive = await archived.CreateArchiveAsync("recover-long-rest-authority.arcbackup");

        await using MaintenanceFixture current = await CreatePaidLifecycleFixtureAsync(protectedSources: false);

        await current.Authority.DisposeAsync();

        await using MemoryErasureRestoreHarness destination = await MemoryErasureRestoreHarness.CreateAsync();

        await SnapshotPaidRestoreInstallationAsync(current, destination);

        CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle markers = new()
        {
            Failure = new Error(ErrorCodes.Covenant.ManualRecoveryRequired, "Injected marker completion interruption."),
        };

        BackupRestoreJournalAnchorStore anchors = new(destination.Credentials,
            new BackupRestoreJournalKeyProvider(destination.Credentials),
            new BackupRestoreJournalInstallationIdentityProvider(destination.Credentials));

        CovenantRestoreStagingServices staging = destination.CovenantStaging(current.Gate) with { Markers = markers };

        BackupRestoreResult restored = await destination.CreateRestoreService(secrets: RestoreAuthoritySecrets(),
            options: new BackupRestoreServiceOptions { EmbeddingDimensions = 64, RestoreStaging = staging })
            .RestoreAsync(new(archive, Confirmed: true, CreateSafetyBackup: false),
                MemoryErasureRestoreHarness.Passphrase.AsMemory(), CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, restored.Status);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, Assert.Single(restored.Issues).Code);

        Assert.NotNull(markers.LastPreparedCheckpoint);

        Assert.NotNull(markers.LastReconcileRequest);

        Assert.Equal(1, markers.ReconcileCalls);

        CovenantExclusiveRecoveryOwner owner = markers.LastReconcileRequest!.Owner;

        string retained = Assert.Single(BackupRestoreStagingIndex.Read(destination.InstallationRoot));

        Assert.True(Directory.Exists(retained));

        // Model a committed candidate produced by an older unsanitized build, using the exact
        // genuine encrypted paid database. No attempt, capability or claim is minted by SQL.
        // The directory identities, plain journal and authenticated envelope remain unchanged.
        string sanitizedDatabase = destination.DatabasePath + ".before-authority-fault";

        File.Move(destination.DatabasePath, sanitizedDatabase);

        await SnapshotPaidRestoreInstallationAsync(source, destination);

        string[] authorityBefore;

        await using (SqliteConnection committed = await destination.OpenLiveDatabaseAsync(
            GrimoireFixture.TestGrimoireSecret))
        {
            Assert.True(await RestoreAuthorityCountAsync(committed, "SELECT count(*) FROM long_rest_provider_effects;") > 0);

            Assert.Equal(0L, await RestoreAuthorityCountAsync(committed, "SELECT count(*) FROM pragma_foreign_key_check;"));

            authorityBefore = await RestoreAuthorityRowsAsync(committed, PaidGenerationTables.Concat(RestoreCanonicalTables)
                .Concat(RestoreBillingTables).Concat(["LongRunningOperations", "session_turn_claims"]));
        }

        byte[] journalBefore = await File.ReadAllBytesAsync(Path.Combine(retained, BackupRestoreJournal.FileName));

        RecordingPostDispositionFinalizer finalizer = new();

        // A restarted process has a new gate with no in-memory retained owner. Reusing the
        // restore's kept-closed gate would refuse duplicate adoption before committed recovery.
        CovenantOperationGate restartedGate = new(current.Runtime, new FakeCovenantCampaignScopeProbe(),
            NullLogger<CovenantOperationGate>.Instance, TimeSpan.FromSeconds(5));

        CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle recoveryMarkers = new()
        {
            Finalizer = finalizer,
        };

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.TryAcquire(destination.InstallationRoot)
            ?? throw new InvalidOperationException("The isolated restore recovery lock could not be acquired.");

        BackupRestoreProfileNamespace profile = LongRestSweepHarness.Require(
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(destination.InstallationRoot));

        BackupRestoreJournalRecoveryState before = LongRestSweepHarness.Require(anchors.Recover(held,
            destination.InstallationRoot, profile, [retained]));

        Assert.Equal(BackupRestoreJournalRecoveryOutcome.Authenticated, before.Outcome);

        Assert.Equal(owner.OperationId, before.OperationId);

        Assert.NotNull(before.Publication);

        BackupRestoreJournalPayloadV2 payload = before.Publication!.Payload;

        Assert.Equal(owner, LongRestSweepHarness.Require(payload.RecoveryOwner()));

        Assert.Equal(BackupRestoreTopologyState.PostSwap, LongRestSweepHarness.Require(
            BackupRestorePhysicalTopology.Reconcile(payload, destination.InstallationRoot, allowEffects: false)));

        BackupRestoreJournalRecord rewrapped = Assert.IsType<BackupRestoreJournalRecord>(BackupRestoreJournal.TryRead(retained));

        Assert.Equal(owner.OperationId, rewrapped.OperationId);

        Assert.True(rewrapped.Phase >= BackupRestorePhase.Reconcile);

        Assert.NotNull(payload.MarkerCleanup);

        Assert.Equal(markers.LastPreparedCheckpoint!.IntentVectorDigest, payload.MarkerCleanup!.IntentVectorDigest);

        Assert.True((await restartedGate.ResumeExclusiveAsync(owner, CancellationToken.None)).IsFailure);

        await using SqliteConnection committedProof = await destination.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        await using SqliteConnection unconfiguredProof = new();

        // Missing proof, actual nonempty committed database and an unconfigured connection
        // must each refuse at the post-swap authority boundary without spending its disposition.
        BackupRestoreRecovery recovery = proofKind switch
        {
            0 => new(destination.InstallationRoot, anchors, restartedGate, recoveryMarkers),
            1 => new(destination.InstallationRoot, anchors, restartedGate, recoveryMarkers, committedProof),
            2 => new(destination.InstallationRoot, anchors, restartedGate, recoveryMarkers, unconfiguredProof),
            _ => throw new ArgumentOutOfRangeException(nameof(proofKind)),
        };

        Assert.Equal(BackupRestorePhysicalRecoveryOutcome.TopologyReady,
            LongRestSweepHarness.Require(await recovery.RecoverPhysicalTopologyBeforeDatabaseAsync(held, CancellationToken.None)));

        Result<BackupRestoreStartupRecoveryOutcome> recovered = await recovery
            .RecoverAuthorityBeforeReadinessAsync(held, CancellationToken.None);

        Assert.True(recovered.IsSuccess);

        // This recorder was not used by the original restore. Its release is reached only after
        // recovery adopted/resumed the owner and ran the post-swap commit arm (even on refusal).
        Assert.Equal(owner.OperationId, recoveryMarkers.ReleasedOwnerOperationId);

        Assert.Equal(BackupRestoreStartupRecoveryOutcome.KeptClosed, recovered.Value);

        Assert.Equal(0, finalizer.Invocations);

        Assert.Equal(0, recoveryMarkers.ReconcileCalls);

        Assert.Equal(1, markers.ReconcileCalls);

        Assert.True(Directory.Exists(retained));

        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(Path.Combine(retained, BackupRestoreJournal.FileName)));

        BackupRestoreJournalRecoveryState active = LongRestSweepHarness.Require(anchors.Recover(held,
            destination.InstallationRoot, profile, [retained]));

        Assert.Equal(BackupRestoreJournalRecoveryOutcome.Authenticated, active.Outcome);

        Assert.Equal(owner.OperationId, active.OperationId);

        Assert.NotNull(before.Publication);

        Assert.NotNull(active.Publication);

        Assert.Equal(before.Publication!.EnvelopeDigest, active.Publication!.EnvelopeDigest);

        Assert.Equal(before.Publication.Anchor, active.Publication.Anchor);

        // Successful resumption proves the recovered closure and exact owner remain adoptable.
        await using CovenantExclusiveLease stillClosed = LongRestSweepHarness.Require(
            await restartedGate.ResumeExclusiveAsync(owner, CancellationToken.None));

        await using SqliteConnection after = await destination.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        Assert.Equal(authorityBefore, await RestoreAuthorityRowsAsync(after, PaidGenerationTables.Concat(RestoreCanonicalTables)
            .Concat(RestoreBillingTables).Concat(["LongRunningOperations", "session_turn_claims"])));
    }
}
