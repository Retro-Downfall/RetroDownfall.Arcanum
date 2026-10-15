using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_committed_topology_retains_its_journal_without_reading_the_secret_then_bootstrap_finishes_it(bool refusePrematureSecretRead)
    {
        (string staging, BackupRestoreJournalRecord journal, RecordingSecretStore secrets) =
            await InterruptCommittedLegacyForPhaseTestsAsync();

        byte[] journalBefore = await File.ReadAllBytesAsync(Path.Combine(staging, BackupRestoreJournal.FileName));

        int secretReads = secrets.GrimoireSecretReads;

        secrets.OnGrimoireSecretRead = () =>
        {
            if (refusePrematureSecretRead) throw new IOException("catalog secret unavailable during topology");
        };

        BackupRestoreRecoveryReport report = Assert.Single(await BackupRestoreRecovery.ResolveAsync(
            _installation, secrets, CancellationToken.None));

        Assert.Equal(secretReads, secrets.GrimoireSecretReads);

        Assert.Equal("CommittedDatabaseVerificationRequired", report.Outcome.ToString());

        Assert.True(Directory.Exists(journal.DisplacedRoot));

        Assert.Equal(journalBefore, await File.ReadAllBytesAsync(Path.Combine(staging, BackupRestoreJournal.FileName)));

        secrets.OnGrimoireSecretRead = null;

        secrets.ApiKey = "legacy-restore-bootstrap-test-key";

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(static provider => provider.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        _ = services.AddGrimoireSchemaInstallation();

        await using ServiceProvider provider = services.BuildServiceProvider();

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.TryAcquire(_installation)
            ?? throw new InvalidOperationException("fixture lock unavailable");

        GrimoireDbPassphraseSource passphrases = new();

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(secrets, passphrases,
            provider.GetRequiredService<IServiceScopeFactory>(), Path.Combine(_installation, "arcanum.db"),
            _installation, held, expectedInstallationId: null, CancellationToken.None);

        Assert.False(Directory.Exists(staging));

        Assert.True(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

        Assert.Equal("# the archived codex", await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));
    }

    private async Task<(string Staging, BackupRestoreJournalRecord Journal, RecordingSecretStore Secrets)>
        InterruptCommittedLegacyForPhaseTestsAsync()
    {
        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("legacy-two-phase.arcbackup");

        RecordingSecretStore secrets = new();

        _ = await Assert.ThrowsAsync<ArgumentException>(() => Restore(secrets,
            new BackupRestoreServiceOptions
            {
                BeforePhaseForTests = phase =>
                {
                    if (phase == BackupRestorePhase.Reconcile) throw new ArgumentException("interrupted after secret rewrap");
                },
            }).RestoreAsync(new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(), CancellationToken.None));

        string staging = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(_installation)!, ".arcanum-restore-*"));

        BackupRestoreJournalRecord journal = Assert.IsType<BackupRestoreJournalRecord>(BackupRestoreJournal.TryRead(staging));

        Assert.Equal(BackupRestorePhase.Reconcile, journal.Phase);

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

        return (staging, journal, secrets);
    }
}
