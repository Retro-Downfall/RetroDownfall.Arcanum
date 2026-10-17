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
    /// <summary>
    /// A new-profile restore whose commit landed but whose post-commit work could not finish leaves its
    /// staging and journal behind, and the default installation's own start finishes that cleanup and
    /// runs.
    /// </summary>
    /// <remarks>
    /// The journal of a new-profile restore is indexed under the default installation, because that is
    /// the live root it names, so the default installation's start is what resolves it. The commit
    /// renamed the staged tree onto a root the journal does not record and never touched the default
    /// installation, and the staged generation was sanitized before it was published. A start that
    /// stopped over the leftover would refuse the whole default installation on every start, with
    /// nothing the operator could do about evidence for a root that is not its own.
    /// </remarks>
    [Fact]
    public async Task A_committed_new_profile_restore_left_in_staging_never_stops_the_default_installation_from_starting()
    {
        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("profile-committed.arcbackup");

        RecordingSecretStore secrets = new()
        {
            GrimoireSecret = fixture.GrimoireSecret,
            ApiKey = "new-profile-startup-test-key",
        };

        string destination = Path.Combine(_root, "second-profile");

        BackupRestoreResult result = await Restore(
                secrets,
                new BackupRestoreServiceOptions
                {
                    BeforePhaseForTests = phase =>
                    {
                        if (phase == BackupRestorePhase.Reconcile)
                        {
                            throw new IOException("injected post-commit fault");
                        }
                    },
                    BeforeReversalRenameForTests = () => throw new IOException("injected reversal fault"),
                })
            .RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.NewProfileRoot,
                    destination,
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);

        // The commit landed: the restored tree is at the new root, and what is left in staging is the
        // journal and an empty shell, with no staged tree in it.
        string staging = Assert.Single(
            Directory.GetDirectories(_root, ".arcanum-restore-*", SearchOption.TopDirectoryOnly));

        Assert.True(File.Exists(Path.Combine(staging, BackupRestoreJournal.FileName)));

        Assert.False(Directory.Exists(Path.Combine(staging, "staged")));

        Assert.True(File.Exists(Path.Combine(destination, "arcanum.db")));

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(static provider => provider.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        _ = services.AddGrimoireSchemaInstallation();

        await using ServiceProvider provider = services.BuildServiceProvider();

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.TryAcquire(_installation)
            ?? throw new InvalidOperationException("fixture lock unavailable");

        GrimoireDbPassphraseSource passphrases = new();

        await GrimoireDatabaseBootstrapper.EnsureInitializedAsync(
            secrets,
            passphrases,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Path.Combine(_installation, "arcanum.db"),
            _installation,
            held,
            expectedInstallationId: null,
            CancellationToken.None);

        Assert.True(provider.GetRequiredService<IGrimoireDbReadiness>().IsReady);

        // The cleanup the restore could not finish is finished, the journal with it, and neither the new
        // root nor the default installation was touched.
        Assert.False(Directory.Exists(staging));

        Assert.True(File.Exists(Path.Combine(destination, "arcanum.db")));

        Assert.Equal(
            "# the archived codex",
            await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));
    }
}
