using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Fact]
    public async Task A_released_pre_swap_restore_owner_cannot_inventory_or_prepare_destination_marker_cleanup()
    {
        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("pre-swap-owner-release.arcbackup");

        InMemoryOsCredentialStore credentials = new();

        using CampaignRootIdentityKeyProvider rootKeys = new(credentials);

        Assert.Equal(
            CampaignRootIdentityKeyState.Created,
            rootKeys.OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        ServiceCollection markerServices = new();

        markerServices.AddSingleton(new PhysicalCampaignRootOpener(rootKeys));

        markerServices.AddSingleton<ICampaignPathMarkerCodec>(new CampaignPathMarkerCodec(rootKeys));

        await using ServiceProvider markerProvider = markerServices.BuildServiceProvider();

        string marker = await SeedOfflineRestoreDestinationMarkerAsync(fixture, markerProvider);

        byte[] markerBefore = await File.ReadAllBytesAsync(marker);

        byte[] databaseBefore = SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath));

        CovenantRestoreStagingTests.RecordingExclusiveGate gate = new();

        CovenantRestoreStagingTests.RecordingRestoreMarkerLifecycle markers = new();

        int inventoryCalls = 0;

        markers.InventoryFactory = () =>
        {
            inventoryCalls++;

            throw new InvalidOperationException("An owner released before staging must not inventory destination roots.");
        };

        BackupRestoreJournalKeyProvider journalKeys = new(credentials);

        BackupRestoreJournalInstallationIdentityProvider identities = new(credentials);

        BackupRestoreJournalAnchorStore anchors = new(credentials, journalKeys, identities);

        BackupRestoreServiceOptions options = new()
        {
            RestoreStaging = new CovenantRestoreStagingServices(
                gate, markers, anchors, identities, journalKeys, new BackupRestoreEffectDigestCalculator()),
            BeforePhaseForTests = phase =>
            {
                if (phase == BackupRestorePhase.Migrate)
                {
                    Assert.NotNull(gate.LastLease);

                    gate.LastLease!.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            },
        };

        BackupRestoreResult result = await Restore(
            new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret }, options)
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(0, inventoryCalls);

        Assert.Null(markers.LastPreparedCheckpoint);

        Assert.Equal(0, markers.ReconcileCalls);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, Assert.Single(result.Issues).Code);

        Assert.Equal(1, gate.ExclusiveAcquisitions);

        Assert.Equal(1, gate.Releases);

        // This recording gate logs attempted dispositions and always answers success. Cleanup may
        // attempt a pre-swap rollback; the real gate refuses a released registration instead of reopening.
        Assert.Equal([CovenantExclusiveLeaseDisposition.RollbackAndReopen], gate.Dispositions);

        Assert.Equal(markerBefore, await File.ReadAllBytesAsync(marker));

        Assert.Equal(databaseBefore, SHA256.HashData(await File.ReadAllBytesAsync(fixture.DatabasePath)));

        await using var unchanged = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal("1", await ScalarAsync(unchanged, "SELECT COUNT(*) FROM campaign_path_identities;"));

        Assert.Equal("0", await ScalarAsync(unchanged, "SELECT COUNT(*) FROM campaign_path_marker_intents;"));
    }
}
