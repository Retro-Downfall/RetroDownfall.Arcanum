using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Fact]
    public async Task A_released_closed_restore_owner_cannot_verify_and_reconcile_the_committed_candidate()
    {
        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("closed-owner-release.arcbackup");

        InMemoryOsCredentialStore credentials = new();

        CovenantRestoreStagingTests.RecordingExclusiveGate gate = new();

        CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle markers = new();

        BackupRestoreJournalAnchorStore anchors = new(credentials,
            new BackupRestoreJournalKeyProvider(credentials), new BackupRestoreJournalInstallationIdentityProvider(credentials));

        BackupRestoreServiceOptions options = new()
        {
            RestoreStaging = new CovenantRestoreStagingServices(gate, markers, anchors,
                new BackupRestoreJournalInstallationIdentityProvider(credentials), new BackupRestoreJournalKeyProvider(credentials),
                new BackupRestoreEffectDigestCalculator()),
            BeforePhaseForTests = phase =>
            {
                if (phase == BackupRestorePhase.Reconcile)
                {
                    Assert.NotNull(gate.LastLease);

                    // The production wrapper records release; no SQL authority row or checkpoint is fabricated.
                    gate.LastLease!.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            },
        };

        BackupRestoreResult result = await Restore(new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret }, options)
            .RestoreAsync(new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(), CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);

        Assert.Equal(1, gate.ExclusiveAcquisitions);

        Assert.Equal(1, gate.Releases);

        Assert.Empty(gate.Dispositions);

        Assert.Equal(0, markers.ReconcileCalls);

        string staging = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(_installation)!, ".arcanum-restore-*"));

        Assert.Equal(BackupRestorePhase.Reconcile, Assert.IsType<BackupRestoreJournalRecord>(BackupRestoreJournal.TryRead(staging)).Phase);

        Assert.True(Directory.Exists(Path.Combine(staging, BackupRestoreJournal.DisplacedDirectoryName)));

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));
    }
}
