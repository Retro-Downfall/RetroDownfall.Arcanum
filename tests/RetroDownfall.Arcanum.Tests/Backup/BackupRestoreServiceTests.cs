using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// End-to-end restore behaviour against a real encrypted archive produced by the real backup
/// service, because the properties under test — atomic commit, complete rollback, migration through
/// the authoritative installer — only exist across the whole pipeline.
/// </summary>
public sealed class BackupRestoreServiceTests : IDisposable
{

    private const string Passphrase = "restore integration passphrase";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-restore-service-" + Guid.NewGuid().ToString("N"));

    private readonly string _installation;

    private readonly string _archives;

    public BackupRestoreServiceTests()
    {

        _installation = Path.Combine(_root, "profile", "arcanum");

        _archives = Path.Combine(_root, "archives");

        Directory.CreateDirectory(_installation);

        Directory.CreateDirectory(_archives);

    }

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task A_full_archive_restores_onto_a_clean_machine_without_the_source_credential_store()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("full.arcbackup");

        WipeInstallation();

        RecordingSecretStore cleanMachine = new();

        BackupRestoreResult result = await Restore(cleanMachine).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db.kdf")));

        Assert.True(File.Exists(Path.Combine(_installation, "CODEX.md")));

        Assert.Equal(fixture.GrimoireSecret, cleanMachine.GrimoireSecret);

        Assert.Null(cleanMachine.ApiKey);

        Assert.Equal(1, result.Reconciliation?.Attachments);

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public async Task Restored_attachment_snapshots_survive_a_workspace_that_no_longer_exists_and_stay_unrefreshable()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("provenance.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(1, result.Reconciliation?.StaleAttachmentSources);

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            "WorkspaceUnavailable",
            await ScalarAsync(connection, "SELECT \"SourceStatus\" FROM \"SessionAttachments\";"));

        Assert.True(
            File.Exists(
                Path.Combine(_installation, "attachments", "session", "note.bin")));

    }

    [Fact]
    public async Task Cross_platform_mappings_rewrite_campaign_workspace_and_provenance_roots()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("mapped.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                PathMappings:
                [
                    new BackupPathMapping(
                        BackupPathMappingKind.CampaignRoot,
                        @"C:\Users\Old\campaigns",
                        "/srv/new/campaigns"),
                    new BackupPathMapping(
                        BackupPathMappingKind.WorkspaceRoot,
                        @"C:\Users\Old\src",
                        "/srv/new/src"),
                ],
                Confirmed: true,
                CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            "/srv/new/campaigns/alpha",
            await ScalarAsync(connection, "SELECT \"Path\" FROM \"Campaigns\";"));

        Assert.Equal(
            "/srv/new/src/project",
            await ScalarAsync(connection, "SELECT \"RootPath\" FROM \"WorkspaceContexts\";"));

        Assert.Equal(
            "/srv/new/src/project/note.txt",
            await ScalarAsync(connection, "SELECT \"SourceCanonicalPath\" FROM \"SessionAttachments\";"));

        Assert.Contains(
            result.Plan.PathMappings,
            static mapping => mapping.Kind == BackupPathMappingKind.CampaignRoot
                && mapping.MatchedTargets > 0);

    }

    [Fact]
    public async Task A_dry_run_reports_the_plan_and_leaves_the_installation_untouched()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("dry.arcbackup");

        string codexBefore = await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md"));

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, DryRun: true),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.DryRunCompleted, result.Status);

        Assert.True(result.Plan.Entries > 0);

        Assert.True(result.Plan.RequiredBytes > result.Plan.RestoredBytes);

        Assert.True(result.Plan.RequiresConfirmation);

        Assert.Equal(codexBefore, await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public async Task A_destructive_replacement_without_confirmation_is_refused()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("unconfirmed.arcbackup");

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_confirmation_required");

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

    }

    [Fact]
    public async Task A_wrong_passphrase_is_refused_before_the_installation_changes()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("wrong.arcbackup");

        string codexBefore = await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md"));

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            "not the passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.authentication_failed");

        Assert.Equal(codexBefore, await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

    }

    [Fact]
    public async Task An_archive_without_portable_recovery_material_is_refused()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync(
            "no-recovery.arcbackup",
            BackupScope.ConfigurationAndAuthoredAssets);

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_recovery_material_missing");

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

    }

    [Fact]
    public async Task A_newer_unsupported_format_is_refused_with_upgrade_guidance()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("newer.arcbackup");

        byte[] bytes = await File.ReadAllBytesAsync(archive);

        bytes[11] = (byte)(BackupArchiveFormat.CurrentVersion + 1);

        await File.WriteAllBytesAsync(archive, bytes);

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(
            result.Issues,
            static candidate => candidate.Code == "backup.restore_format_newer");

        Assert.Contains("upgrade", issue.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

    }

    [Fact]
    public async Task Insufficient_destination_space_is_refused_before_staging()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("cramped.arcbackup");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                new BackupRestoreServiceOptions { AvailableBytesOverrideForTests = 1024 })
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_insufficient_disk");

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public async Task A_fault_after_commit_returns_the_prior_installation_to_its_original_state()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("faulted.arcbackup");

        await File.WriteAllTextAsync(
            Path.Combine(_installation, "CODEX.md"),
            "# the original codex");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                new BackupRestoreServiceOptions
                {

                    BeforePhaseForTests = phase =>
                    {

                        if (phase == BackupRestorePhase.Reconcile)
                        {

                            throw new IOException("injected post-commit fault");

                        }

                    },

                })
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.RolledBack, result.Status);

        Assert.Equal(
            "# the original codex",
            await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public async Task A_fault_before_commit_leaves_the_installation_unchanged()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("early-fault.arcbackup");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                new BackupRestoreServiceOptions
                {

                    BeforePhaseForTests = phase =>
                    {

                        if (phase == BackupRestorePhase.Validate)
                        {

                            throw new IOException("injected pre-commit fault");

                        }

                    },

                })
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public async Task The_data_protection_key_ring_and_existing_backups_survive_a_replacement()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("preserve.arcbackup");

        string keyRing = Path.Combine(_installation, "keys", "key-local.xml");

        Directory.CreateDirectory(Path.GetDirectoryName(keyRing)!);

        await File.WriteAllTextAsync(keyRing, "<key/>");

        string existingBackup = Path.Combine(_installation, "backups", "older.arcbackup");

        Directory.CreateDirectory(Path.GetDirectoryName(existingBackup)!);

        await File.WriteAllTextAsync(existingBackup, "older");

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal("<key/>", await File.ReadAllTextAsync(keyRing));

        Assert.Equal("older", await File.ReadAllTextAsync(existingBackup));

    }

    /// <summary>
    /// The commit preserves the destination's machine-local entries one at a time, so a fault on the
    /// second one leaves the first already inside the new tree. Reversal must be driven by that
    /// filesystem evidence rather than by the commit's own success flag — otherwise the key ring
    /// rides into staging and the cleanup deletes the only copy of it.
    /// </summary>
    [Fact]
    public async Task A_commit_that_fails_partway_through_preserving_still_returns_the_key_ring()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("partial-preserve.arcbackup");

        await File.WriteAllTextAsync(
            Path.Combine(_installation, "CODEX.md"),
            "# the original codex");

        string keyRing = Path.Combine(_installation, "keys", "key-local.xml");

        Directory.CreateDirectory(Path.GetDirectoryName(keyRing)!);

        await File.WriteAllTextAsync(keyRing, "<key/>");

        string existingBackup = Path.Combine(_installation, "backups", "older.arcbackup");

        Directory.CreateDirectory(Path.GetDirectoryName(existingBackup)!);

        await File.WriteAllTextAsync(existingBackup, "older");

        bool faulted = false;

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                new BackupRestoreServiceOptions
                {

                    BeforePreservedEntryMoveForTests = name =>
                    {

                        if (name == "backups" && !faulted)
                        {

                            faulted = true;

                            throw new IOException("injected preserve fault");

                        }

                    },

                })
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.True(faulted);

        Assert.Equal(BackupRestoreStatus.RolledBack, result.Status);

        Assert.Equal("<key/>", await File.ReadAllTextAsync(keyRing));

        Assert.Equal("older", await File.ReadAllTextAsync(existingBackup));

        Assert.Equal(
            "# the original codex",
            await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    /// <summary>
    /// A rollback that cannot be verified must never be reported as clean, because the displaced
    /// tree in staging is then the operator's only surviving installation. Retention of evidence
    /// outranks tidiness: the journal and the staging root stay for startup recovery to resolve.
    /// </summary>
    [Fact]
    public async Task A_reversal_that_cannot_complete_keeps_the_journal_and_the_displaced_installation()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("stranded.arcbackup");

        await File.WriteAllTextAsync(
            Path.Combine(_installation, "CODEX.md"),
            "# the original codex");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                new BackupRestoreServiceOptions
                {

                    BeforePhaseForTests = phase =>
                    {

                        if (phase == BackupRestorePhase.Reconcile)
                        {

                            throw new IOException("injected post-commit fault");

                        }

                    },

                    BeforeReversalRenameForTests =
                        static () => throw new IOException("injected reversal fault"),

                })
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_reversal_incomplete");

        string staging = Assert.Single(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

        Assert.Contains(staging, result.Issues[0].Message, StringComparison.Ordinal);

        BackupRestoreJournalRecord journal = Assert.IsType<BackupRestoreJournalRecord>(
            BackupRestoreJournal.TryRead(staging));

        // Rewound so startup recovery resolves the roots from evidence instead of reading a later
        // phase as "the commit finished; only cleanup remained" and discarding the displaced tree.
        Assert.Equal(BackupRestorePhase.Commit, journal.Phase);

        Assert.Equal(
            "# the original codex",
            await File.ReadAllTextAsync(Path.Combine(journal.DisplacedRoot, "CODEX.md")));

    }

    [Fact]
    public async Task A_new_profile_root_restore_leaves_the_current_installation_and_its_secrets_alone()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("profile.arcbackup");

        await File.WriteAllTextAsync(
            Path.Combine(_installation, "CODEX.md"),
            "# still the original");

        RecordingSecretStore store = new()
        {

            GrimoireSecret = "the current machine secret",

        };

        string destination = Path.Combine(_root, "second-profile");

        BackupRestoreResult result = await Restore(store).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.NewProfileRoot,
                destination,
                Confirmed: true,
                CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.True(File.Exists(Path.Combine(destination, "arcanum.db")));

        Assert.Equal(
            "# still the original",
            await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

        Assert.Equal("the current machine secret", store.GrimoireSecret);

    }

    /// <summary>
    /// A new-profile restore must stage beside its destination so commit stays a same-volume rename,
    /// which is somewhere startup recovery's sweep of the live root's parent can never reach. The
    /// staging index is the trail back, so a process death does not strand decrypted archive
    /// contents on disk forever.
    /// </summary>
    [Fact]
    public async Task A_new_profile_restore_records_its_distant_staging_root_for_startup_recovery()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("indexed-profile.arcbackup");

        string elsewhere = Path.Combine(_root, "another-volume");

        List<string> recordedDuringRestore = [];

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                new BackupRestoreServiceOptions
                {

                    BeforePhaseForTests = phase =>
                    {

                        if (phase == BackupRestorePhase.Commit)
                        {

                            recordedDuringRestore.AddRange(
                                BackupRestoreStagingIndex.Read(_installation));

                        }

                    },

                })
            .RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.NewProfileRoot,
                    Path.Combine(elsewhere, "second-profile"),
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        string staging = Assert.Single(recordedDuringRestore);

        Assert.Equal(Path.GetFullPath(elsewhere), Path.GetDirectoryName(staging));

        Assert.True(
            BackupRestoreJournal.IsCanonicalStagingName(Path.GetFileName(staging)));

        Assert.Empty(BackupRestoreStagingIndex.Read(_installation));

    }

    [Fact]
    public async Task A_new_profile_root_must_be_empty_and_may_not_be_the_current_installation()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("profile-guard.arcbackup");

        BackupRestoreResult occupied = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.NewProfileRoot,
                _archives,
                Confirmed: true,
                CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Contains(
            occupied.Issues,
            static issue => issue.Code == "backup.restore_destination_not_empty");

        BackupRestoreResult current = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.NewProfileRoot,
                _installation,
                Confirmed: true,
                CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Contains(
            current.Issues,
            static issue => issue.Code == "backup.restore_destination_is_current");

    }

    [Fact]
    public async Task Selected_sessions_import_into_a_live_installation_without_replacing_it()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import.arcbackup");

        await File.WriteAllTextAsync(
            Path.Combine(_installation, "CODEX.md"),
            "# untouched by import");

        RecordingSecretStore store = new()
        {

            GrimoireSecret = fixture.GrimoireSecret,

        };

        BackupRestoreResult result = await Restore(store).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.ImportSelectedSessions,
                SessionIds: [Fixture.SessionId],
                Confirmed: true,
                CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(
            "# untouched by import",
            await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            "2",
            await ScalarAsync(connection, "SELECT COUNT(*) FROM \"Sessions\";"));

        // The destination's own attachment keeps its live provenance; only the imported copy is
        // demoted, because its recorded workspace path belongs to another machine.
        Assert.Equal(
            "1",
            await ScalarAsync(
                connection,
                """
                SELECT COUNT(*) FROM "SessionAttachments"
                WHERE "SourceStatus" = 'WorkspaceUnavailable';
                """));

        Assert.Equal(
            "1",
            await ScalarAsync(
                connection,
                """
                SELECT COUNT(*) FROM "SessionAttachments" WHERE "SourceStatus" = 'Refreshable';
                """));

    }

    [Fact]
    public async Task Importing_a_session_the_archive_does_not_contain_is_refused()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-missing.arcbackup");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret })
            .RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.ImportSelectedSessions,
                    SessionIds: [Guid.Parse("99999999-9999-9999-9999-999999999999")],
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_import_session_absent");

    }

    [Fact]
    public async Task An_all_interfaces_acknowledgement_is_not_inherited_by_the_destination()
    {

        Fixture fixture = await CreateFixtureAsync(listenAny: true);

        string archive = await fixture.CreateBackupAsync("listen-any.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        string configuration = await File.ReadAllTextAsync(
            Path.Combine(_installation, "arcanum.json"));

        Assert.Contains("\"ListenAny\": false", configuration, StringComparison.Ordinal);

        Assert.Contains(
            result.Plan.Warnings,
            static warning => warning.Contains("ListenAny", StringComparison.Ordinal));

    }

    [Fact]
    public async Task Trusted_workspace_metadata_is_withheld_rather_than_inherited_as_authorization()
    {

        Fixture fixture = await CreateFixtureAsync();

        await File.WriteAllTextAsync(
            Path.Combine(_installation, "trusted-mcp-workspaces.json"),
            "{\"workspaces\":[]}");

        string archive = await fixture.CreateBackupAsync(
            "trusted.arcbackup",
            include: [BackupComponent.TrustedMcpWorkspaceMetadata]);

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.False(
            File.Exists(Path.Combine(_installation, "trusted-mcp-workspaces.json")));

        Assert.Contains(
            result.Plan.Warnings,
            static warning => warning.Contains("Re-approve", StringComparison.Ordinal));

    }

    [Fact]
    public async Task The_master_api_key_is_adopted_only_on_explicit_request()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync(
            "master.arcbackup",
            include: [BackupComponent.MasterApiKey]);

        WipeInstallation();

        RecordingSecretStore withoutRequest = new();

        _ = await Restore(withoutRequest).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Null(withoutRequest.ApiKey);

    }

    [Fact]
    public async Task A_restore_is_refused_while_another_process_holds_the_maintenance_lock()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("locked.arcbackup");

        using ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(_installation);

        Assert.NotNull(held);

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_maintenance_unavailable");

    }

    [Fact]
    public async Task An_older_supported_snapshot_converges_through_the_authoritative_schema_installer()
    {

        Fixture fixture = await CreateFixtureAsync();

        await DropTableAsync(fixture, "Prompts");

        string archive = await fixture.CreateBackupAsync("older-schema.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.True(result.Plan.SchemaMigrationRequired);

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            "1",
            await ScalarAsync(
                connection,
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'Prompts';"));

    }

    [Fact]
    public async Task A_pre_restore_safety_backup_is_produced_before_the_destructive_step()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("safety.arcbackup");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret },
                safetyBackups: fixture.BackupService)
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        string safety = Assert.IsType<string>(result.SafetyBackupPath);

        Assert.True(File.Exists(safety));

        Assert.Contains(
            result.Phases,
            static phase => phase.Phase == BackupRestorePhase.SafetyPoint);

    }

    [Fact]
    public async Task A_pre_restore_safety_backup_that_does_not_complete_stops_the_restore_before_the_destructive_step()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("safety-incomplete.arcbackup");

        string sentinel = Path.Combine(_installation, "only-in-the-live-tree.md");

        await File.WriteAllTextAsync(sentinel, "the operator's only copy");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret },
                safetyBackups: new IncompleteBackupService())
            .RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.True(File.Exists(sentinel));

        Assert.True(File.Exists(Path.Combine(_installation, "arcanum.db")));

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_safety_backup_failed");

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.safety_inventory_incomplete");

        Assert.Null(result.SafetyBackupPath);

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

    }

    [Fact]
    public async Task A_migrated_archive_is_rewritten_at_the_current_format_without_touching_the_source()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("migrate-source.arcbackup");

        byte[] before = await File.ReadAllBytesAsync(archive);

        string output = Path.Combine(_archives, "migrate-output.arcbackup");

        BackupMigrateResult result = await Restore(new RecordingSecretStore()).MigrateAsync(
            new BackupMigrateRequest(archive, output),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Empty(result.Issues);

        Assert.True(result.Migrated);

        Assert.Equal(BackupArchiveFormat.CurrentVersion, result.OutputFormatVersion);

        Assert.True(File.Exists(output));

        Assert.Equal(before, await File.ReadAllBytesAsync(archive));

        BackupVerifyResult verified = await Codec().VerifyAsync(
            output,
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.True(verified.IsValid);

    }

    [Fact]
    public async Task Migrating_onto_the_source_path_or_an_existing_output_is_refused()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("migrate-guard.arcbackup");

        BackupMigrateResult sameFile = await Restore(new RecordingSecretStore()).MigrateAsync(
            new BackupMigrateRequest(archive, archive),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Contains(
            sameFile.Issues,
            static issue => issue.Code == "backup.migrate_output_is_source");

        string occupied = Path.Combine(_archives, "occupied.arcbackup");

        await File.WriteAllTextAsync(occupied, "existing");

        BackupMigrateResult exists = await Restore(new RecordingSecretStore()).MigrateAsync(
            new BackupMigrateRequest(archive, occupied),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Contains(
            exists.Issues,
            static issue => issue.Code == "backup.migrate_output_exists");

        Assert.Equal("existing", await File.ReadAllTextAsync(occupied));

    }

    private BackupRestoreService Restore(
        ISecretStore secretStore,
        BackupRestoreServiceOptions? options = null,
        IBackupService? safetyBackups = null) =>
        new(
            Paths(),
            Codec(),
            secretStore,
            safetyBackups is null ? null : () => safetyBackups,
            TimeProvider.System,
            options ?? new BackupRestoreServiceOptions());

    private BackupStatePaths Paths() => new(
        _installation,
        _installation,
        Path.Combine(_installation, "audit.jsonl"),
        Path.Combine(_installation, "guardrails.jsonl"));

    private static BackupArchiveCodec Codec() =>
        new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            ChunkSize = 64 * 1024,

        });

    private void WipeInstallation()
    {

        Directory.Delete(_installation, recursive: true);

        Directory.CreateDirectory(_installation);

    }

    private async Task<SqliteConnection> OpenRestoredAsync(string grimoireSecret) =>
        await BackupRestoreDatabaseWorker.OpenAsync(
            Path.Combine(_installation, "arcanum.db"),
            grimoireSecret,
            readOnly: true,
            CancellationToken.None);

    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? null : Convert.ToString(value);

    }

    private static async Task DropTableAsync(Fixture fixture, string table)
    {

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
            fixture.DatabasePath,
            fixture.GrimoireSecret,
            readOnly: false,
            CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"DROP TABLE IF EXISTS \"{table}\";";

        _ = await command.ExecuteNonQueryAsync();

    }

    private async Task<Fixture> CreateFixtureAsync(bool listenAny = false)
    {

        Fixture fixture = new(_installation, _archives, Paths(), Codec());

        await fixture.BuildAsync(listenAny);

        return fixture;

    }

    /// <summary>
    /// A believable installation: a real SQLCipher Grimoire holding a Session, an attachment with
    /// workspace provenance, a Campaign, and a workspace context, plus configuration and authored
    /// assets — backed up by the real <see cref="BackupService"/>.
    /// </summary>
    private sealed class Fixture(
        string installation,
        string archives,
        BackupStatePaths paths,
        BackupArchiveCodec codec)
    {

        public static readonly Guid SessionId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");

        public string GrimoireSecret { get; } = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        public string DatabasePath => Path.Combine(installation, "arcanum.db");

        public IBackupService BackupService { get; private set; } = null!;

        public async Task BuildAsync(bool listenAny)
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(installation);

            await File.WriteAllTextAsync(
                Path.Combine(installation, "arcanum.json"),
                $$"""
                {
                  "Arcanum": {
                    "Host": { "ListenAny": {{(listenAny ? "true" : "false")}} },
                    "Workspace": { "DefaultRoot": "C:\\Users\\Old\\src\\project" }
                  }
                }
                """);

            await File.WriteAllTextAsync(
                Path.Combine(installation, "CODEX.md"),
                "# the archived codex");

            string attachment = Path.Combine(installation, "attachments", "session", "note.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(attachment)!);

            await File.WriteAllTextAsync(attachment, "attachment bytes");

            await BuildDatabaseAsync();

            BackupService = new BackupService(
                paths,
                new BackupInventoryPlanner(paths),
                new BackupDatabaseSnapshotter(),
                codec,
                new FixtureSecretReader(GrimoireSecret),
                TimeProvider.System);

        }

        public async Task<string> CreateBackupAsync(
            string name,
            BackupScope scope = BackupScope.Full,
            BackupComponent[]? include = null)
        {

            string archive = Path.Combine(archives, name);

            BackupCreateResult created = await BackupService.CreateAsync(
                new BackupCreateRequest(
                    new BackupPlanRequest(scope, SessionId: null, include ?? [], Exclude: []),
                    archive,
                    Overwrite: true),
                Passphrase.AsMemory(),
                CancellationToken.None);

            Assert.Equal(BackupCreateStatus.Complete, created.Status);

            return archive;

        }

        private async Task BuildDatabaseAsync()
        {

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(
                GrimoireKeyDerivation.KdfVersion2);

            GrimoireKdfSidecarFile.Write(DatabasePath, sidecar);

            byte[] salt = sidecar.GetSaltBytes();

            string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                GrimoireSecret,
                salt);

            CryptographicOperations.ZeroMemory(salt);

            SqliteNativeRuntime.Instance.Initialize();

            await using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {

                    DataSource = DatabasePath,

                    Password = passphrase,

                    Pooling = false,

                }.ToString());

            await connection.OpenAsync();

            _ = await RetroDownfall.Arcanum.Infrastructure.Data.Schema.GrimoireSchemaInstaller
                .InstallAsync(connection, 1536, logger: null, CancellationToken.None);

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = """
                INSERT INTO "Campaigns"
                    ("Id", "Name", "NameLower", "Path", "Type", "Description", "Settings",
                     "SanctumConfigJson", "CreatedAt", "UpdatedAt")
                VALUES ('22222222-2222-2222-2222-222222222222', 'Alpha', 'alpha',
                        'C:\Users\Old\campaigns\alpha', 0, NULL, '{}',
                        '{"allowedPaths":["C:\\Users\\Old\\src\\project"]}',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "WorkspaceContexts" ("Id", "RootPath", "SerializedSnapshot", "CreatedAt")
                VALUES ('33333333-3333-3333-3333-333333333333', 'C:\Users\Old\src\project', '{}',
                        '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('11111111-1111-1111-1111-111111111111', NULL, 'Archived session', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Entries"
                    ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ('44444444-4444-4444-4444-444444444444',
                        '11111111-1111-1111-1111-111111111111', 0, 'hello', 'test',
                        '2026-01-01T00:00:00Z', 1);

                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                     "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                     "SourceKind", "SourceCanonicalPath", "SourceStatus", "EncryptionVersion")
                VALUES ('55555555-5555-5555-5555-555555555555',
                        '11111111-1111-1111-1111-111111111111', 'Bound', 'note', 'note.txt', 1,
                        'session/note.bin', 'abc', 'text/plain', 16, 'Text',
                        '2026-01-01T00:00:00Z', 'WorkspaceFile',
                        'C:\Users\Old\src\project\note.txt', 'Refreshable', 0);
                """;

            _ = await seed.ExecuteNonQueryAsync();

        }

        private sealed class FixtureSecretReader(string grimoireSecret) : IBackupSecretSnapshotReader
        {

            public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
                Task.FromResult(SecretStoreReadResult.Ok(grimoireSecret));

            public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
                Task.FromResult(
                    SecretStoreReadResult.Ok(
                        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

            public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
                Task.FromResult(SecretStoreReadResult.Ok("archived-master-key"));

        }

    }

    /// <summary>
    /// A safety-backup service whose create reports <see cref="BackupCreateStatus.Incomplete"/>
    /// without throwing — the graceful outcome the real <see cref="BackupService"/> returns when a
    /// required component cannot be inventoried or a required secret cannot be read.
    /// </summary>
    private sealed class IncompleteBackupService : IBackupService
    {

        public Task<BackupPlan> PlanAsync(
            BackupPlanRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackupCreateResult> CreateAsync(
            BackupCreateRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new BackupCreateResult(
                    BackupCreateStatus.Incomplete,
                    ArchivePath: null,
                    ArchiveBytes: 0,
                    Guid.NewGuid(),
                    Manifest: null,
                    new BackupPlan(
                        DateTimeOffset.UnixEpoch,
                        BackupScope.Full,
                        SessionId: null,
                        [],
                        0,
                        0,
                        [],
                        []),
                    [
                        new BackupVerifyIssue(
                            "backup.safety_inventory_incomplete",
                            "A required component could not be inventoried."),
                    ]));

        public Task<BackupInspectResult> InspectAsync(
            string archivePath,
            ReadOnlyMemory<char>? recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackupVerifyResult> VerifyAsync(
            string archivePath,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BackupListItem>> ListAsync(
            string? directory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingSecretStore : ISecretStore
    {

        public string? ApiKey { get; set; }

        public string? GrimoireSecret { get; set; }

        public string? FileEncryptionSecret { get; set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                ApiKey is null ? SecretStoreReadResult.Missing() : SecretStoreReadResult.Ok(ApiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {

            ApiKey = apiKey;

            return Task.CompletedTask;

        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult(GrimoireSecret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            GrimoireSecret = encryptionSecret;

            return Task.CompletedTask;

        }

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(
                FileEncryptionSecret is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(FileEncryptionSecret));

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
        {

            FileEncryptionSecret = encryptionSecret;

            return Task.CompletedTask;

        }

    }

}
