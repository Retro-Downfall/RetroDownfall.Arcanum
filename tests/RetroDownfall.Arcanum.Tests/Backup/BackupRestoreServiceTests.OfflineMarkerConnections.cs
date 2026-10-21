using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_protected_replacement_cleans_destination_markers_using_only_restore_owned_connections(
        bool createSafetyBackup)
    {
        using BackupRestoreServiceTests sourceInstallation = new();

        Fixture source = await sourceInstallation.CreateFixtureAsync();

        string archive = await source.CreateBackupAsync("offline-marker-source.arcbackup");

        Fixture destination = await CreateFixtureAsync();

        Assert.NotEqual(source.GrimoireSecret, destination.GrimoireSecret);

        InMemoryOsCredentialStore credentials = new();

        RecordingSecretStore secrets = new()
        {
            ApiKey = "offline-marker-destination-api-key",
            GrimoireSecret = destination.GrimoireSecret,
            FileEncryptionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };

        OfflineRestoreForbiddenConnections ordinaryConnections = new();

        ServiceCollection services = new();

        services.AddLogging();

        services.AddArcanumCliClientStack();

        services.Configure<ArcanumSettings>(settings => settings.Features.Covenant = true);

        services.RemoveAll<BackupStatePaths>();

        services.AddSingleton(Paths());

        services.RemoveAll<BackupArchiveCodec>();

        services.AddSingleton(Codec());

        services.RemoveAll<IOsCredentialStore>();

        services.AddSingleton<IOsCredentialStore>(credentials);

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(secrets);

        // The real fixture backup service already owns this destination's explicit secret and paths.
        services.RemoveAll<IBackupService>();

        services.AddSingleton(destination.BackupService);

        services.RemoveAll<GrimoireSchemaInstaller>();

        services.AddSingleton(GrimoireSchemaTestInstaller.Create());

        services.RemoveAll<ICovenantOperationGate>();

        services.AddSingleton<ICovenantOperationGate>(CovenantOperationGateFixture.CreateGate());

        services.RemoveAll<ICovenantConnectionSource>();

        services.AddSingleton<ICovenantConnectionSource>(ordinaryConnections);

        services.RemoveAll<InstallationMaintenanceCoordination>();

        services.AddSingleton(new InstallationMaintenanceCoordination(
            _installation,
            new ClientMutationBlockerStore(_installation),
            new InstallationResetClientMutationEvidenceProbe(new InstallationStartupProbe(
                _installation,
                Path.Combine(_installation, "arcanum.json"),
                destination.DatabasePath,
                Path.Combine(_installation, "security.dat"),
                credentials)),
            new BackupRestoreClientMutationEvidenceProbe(_installation, credentials)));

        await using ServiceProvider provider = services.BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        IServiceProvider scoped = scope.ServiceProvider;

        Assert.Equal(
            CampaignRootIdentityKeyState.Created,
            scoped.GetRequiredService<ICampaignRootIdentityKeyCreator>()
                .OpenOrCreateRootIdentityKey(registeredRootsExist: false));

        string marker = await SeedOfflineRestoreDestinationMarkerAsync(destination, scoped);

        Assert.True(File.Exists(marker));

        IGrimoireDbPassphraseSource livePassphrase = scoped.GetRequiredService<IGrimoireDbPassphraseSource>();

        Assert.Throws<InvalidOperationException>(() => _ = livePassphrase.Passphrase);

        string? priorDataset;

        await using (SqliteConnection current = await BackupRestoreDatabaseWorker.OpenAsync(
            destination.DatabasePath, destination.GrimoireSecret, readOnly: true, CancellationToken.None))
        {
            priorDataset = await ScalarAsync(current, "SELECT hex(DatasetGeneration) FROM covenant_state;");
        }

        FileEncryptionKeyProvider liveKeys = scoped.GetRequiredService<FileEncryptionKeyProvider>();

        string originalKey = (await liveKeys.GetForWriteAsync()).KeyId;

        IBackupRestoreService restore = scoped.GetRequiredService<IBackupRestoreService>();

        BackupRestoreRequest request = new(archive, Confirmed: true, CreateSafetyBackup: createSafetyBackup);

        BackupRestorePlan plan = await restore.PlanAsync(request, Passphrase.AsMemory(), CancellationToken.None);

        Assert.Empty(plan.Blockers);

        Assert.Equal(0, ordinaryConnections.Calls);

        Assert.Throws<InvalidOperationException>(() => _ = livePassphrase.Passphrase);

        BackupRestoreResult result = await restore.RestoreAsync(
            request,
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(0, ordinaryConnections.Calls);

        Assert.True(
            result.Status == BackupRestoreStatus.Completed,
            $"{result.Status}: {string.Join("; ", result.Issues.Select(issue => issue.Code + ": " + issue.Message))}");

        Assert.Empty(result.Issues);

        Assert.Throws<InvalidOperationException>(() => _ = livePassphrase.Passphrase);

        Assert.False(File.Exists(marker));

        Assert.Equal(source.GrimoireSecret, secrets.GrimoireSecret);

        using FileEncryptionKeyProvider persistedKeys = new(secrets);

        string restoredKey = (await persistedKeys.GetForWriteAsync()).KeyId;

        Assert.NotEqual(originalKey, restoredKey);

        Assert.Equal(restoredKey, (await liveKeys.GetForWriteAsync()).KeyId);

        Assert.Equal(createSafetyBackup, result.SafetyBackupPath is not null);

        await using SqliteConnection committed = await OpenRestoredAsync(source.GrimoireSecret);

        Assert.False(string.IsNullOrEmpty(priorDataset));

        Assert.NotEqual(priorDataset, await ScalarAsync(committed, "SELECT hex(DatasetGeneration) FROM covenant_state;"));

        Assert.Equal("0", await ScalarAsync(committed, "SELECT COUNT(*) FROM campaign_path_identities;"));

        Assert.Equal("1", await ScalarAsync(committed,
            "SELECT COUNT(*) FROM campaign_path_marker_intents WHERE IntentKindCode = 3 AND PhaseCode = 12;"));

        Assert.Equal("1", await ScalarAsync(committed,
            "SELECT COUNT(*) FROM \"Sessions\" WHERE \"Id\" = '11111111-1111-1111-1111-111111111111';"));
    }

    private async Task<string> SeedOfflineRestoreDestinationMarkerAsync(
        Fixture destination,
        IServiceProvider services)
    {
        string directory = Directory.CreateDirectory(Path.Combine(_root, "destination-campaign")).FullName;

        PhysicalCampaignRootOpener opener = services.GetRequiredService<PhysicalCampaignRootOpener>();

        ICampaignPathMarkerCodec codec = services.GetRequiredService<ICampaignPathMarkerCodec>();

        CovenantDigest identity = opener.IdentifyExact(directory)!.Value;

        var opened = await CampaignPathMarkerRootAuthority.Instance.OpenAsync(
            opener,
            Fixture.CampaignId,
            3,
            identity,
            directory,
            CancellationToken.None);

        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : string.Empty);

        await using CampaignPathMarkerRootAuthority authority = opened.Value;

        Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(directory, out FileHandleMetadata metadata));

        var encoded = codec.Encode(new CampaignPathMarkerContent(
            CampaignPathMarkerPolicy.Version,
            Fixture.CampaignId,
            3,
            metadata.Identity.VolumeId,
            metadata.Identity.FileId,
            [.. Enumerable.Repeat((byte)0x42, CampaignPathMarkerPolicy.MarkerSecretByteCount)]));

        Assert.True(encoded.IsSuccess, encoded.IsFailure ? encoded.Error.Message : string.Empty);

        PhysicalCampaignRootOpener.MarkerTemporaryHandleCapability temporary =
            (await authority.CreateTemporaryExclusiveNoFollowAsync("marker-offline-restore.tmp", CancellationToken.None)).Value;

        Assert.True((await temporary.WriteAllAsync(encoded.Value, CancellationToken.None)).IsSuccess);

        Assert.True((await temporary.FlushToDiskAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await authority.RenameTemporaryToMarkerNoReplaceAsync(
            temporary, temporary.PhysicalIdentityDigest, encoded.Value, CancellationToken.None)).IsSuccess);

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            destination.DatabasePath, destination.GrimoireSecret, readOnly: false, CancellationToken.None);

        await using SqliteCommand insert = connection.CreateCommand();

        insert.CommandText = """
            INSERT INTO campaign_path_identities (
                CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest, UpdatedAtUtc)
            VALUES ($campaign, 1, 3, $path, 1, $identity, '2026-10-21T00:00:00.0000000+00:00');
            """;

        insert.Parameters.AddWithValue("$campaign", Fixture.CampaignId.ToString("D"));

        insert.Parameters.AddWithValue("$path", directory);

        insert.Parameters.AddWithValue("$identity", identity.Bytes.ToArray());

        await insert.ExecuteNonQueryAsync();

        return Path.Combine(directory, ".arcanum", "campaign-root.marker");
    }

    private sealed class OfflineRestoreForbiddenConnections : ICovenantConnectionSource
    {
        internal int Calls { get; private set; }

        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {
            Calls++;

            throw new InvalidOperationException("An offline replacement must use its restore-owned database generation.");
        }

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            GetOpenConnectionAsync(cancellationToken);
    }
}
