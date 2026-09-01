using System.Security.Cryptography;

using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

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

    /// <summary>
    /// An archive carries the Saga store's retirement evidence and the key that binds it, byte for byte.
    /// </summary>
    /// <remarks>
    /// No list in the backup pipeline names either table, and that is the point rather than an omission:
    /// the database component is a page-level copy of the whole encrypted file, so a table joins a
    /// backup by existing. This case is what says so, and what would notice if that ever stopped being
    /// true.
    ///
    /// <para>Asserted on the bytes, not on a row count. The failure worth guarding against is a restore
    /// that converges the schema and leaves both tables present and empty — two tables restored, by any
    /// count, and every retirement the operator made silently undone. A digest is evidence of nothing
    /// without the key it was computed under, so the pair has to arrive together and unchanged.</para>
    ///
    /// <para>The Campaign the suppression names is asserted too, because the restore rewrites Campaign
    /// roots for a cross-platform archive and knows nothing about this table.</para>
    /// </remarks>
    [Fact]
    public async Task A_full_archive_carries_saga_retirement_evidence_and_the_key_that_binds_it()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("curation.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            Fixture.SuppressionKeyMaterialHex,
            await ScalarAsync(
                connection,
                "SELECT upper(hex(KeyMaterial)) FROM saga_suppression_key WHERE KeyId = 1;"));

        Assert.Equal(
            Fixture.SuppressionDigestHex,
            await ScalarAsync(
                connection,
                "SELECT upper(hex(SuppressionDigest)) FROM saga_retirement_suppressions;"));

        Assert.Equal(
            Fixture.CampaignId.ToString("D"),
            await ScalarAsync(
                connection,
                "SELECT CampaignId FROM saga_retirement_suppressions;"));

    }

    /// <summary>
    /// A restore cancelled while it is composing the staged generation stops there rather than
    /// finishing the copy first.
    /// </summary>
    /// <remarks>
    /// Composition is the second full-size write of everything the extraction already wrote — for a
    /// multi-gigabyte Grimoire, minutes of blocking copying inside a single phase. Entered at
    /// <see cref="BackupRestoreService.RestoreAsync"/> with the operator's own token, because what is
    /// under test is whether pressing Ctrl-C during that window is observed at all.
    ///
    /// <para>Asserted on how many entries were composed rather than on what survives on disk: the
    /// staging root is removed as the cancellation unwinds, which is correct and also erases the
    /// evidence, so the count is taken as the copy runs.</para>
    /// </remarks>
    [Fact]
    public async Task A_cancelled_restore_stops_composing_the_staged_tree()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("compose-cancel.arcbackup");

        WipeInstallation();

        using CancellationTokenSource cancellation = new();

        List<string> composed = [];

        BackupRestoreServiceOptions options = new()
        {

            BeforeStagedEntryComposeForTests = entry =>
            {

                composed.Add(entry);

                cancellation.Cancel();

            },

        };

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Restore(new RecordingSecretStore(), options).RestoreAsync(
                new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                cancellation.Token));

        Assert.Single(composed);

    }

    /// <summary>
    /// An archive holding two entries whose paths differ only in case is refused rather than restored
    /// with one file standing in for both.
    /// </summary>
    /// <remarks>
    /// Entered at <see cref="BackupRestoreService.RestoreAsync"/> over a real archive the real backup
    /// service wrote, because the collision is only interesting where it is invisible: extraction
    /// tracks entries ordinally and writes each one with a create-or-truncate open, the manifest
    /// comparison verifies against hashes taken from the decrypted stream rather than from the files
    /// on disk, and staging then copies both entries over each other. Every check passes and the
    /// restore reports completed while one attachment silently carries the other's bytes.
    ///
    /// <para>The refusal asserted here is unconditional rather than filesystem-dependent, and that is
    /// deliberate: whether the two entries collide on the destination volume is a property of the
    /// machine the archive lands on rather than of the archive, and an archive that is only safe on
    /// some volumes is not one this build should lay down on any of them.</para>
    /// </remarks>
    [Fact]
    public async Task An_archive_whose_entry_paths_differ_only_in_case_is_refused()
    {

        Fixture fixture = await CreateFixtureAsync(caseCollidingAttachment: true);

        string archive = await fixture.CreateBackupAsync("case-collision.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(result.Issues, static issue => issue.Code == "backup.invalid_archive");

        // Refused before anything was laid down: the destination is as empty as the wipe left it.
        Assert.False(File.Exists(Path.Combine(_installation, "arcanum.db")));

    }

    /// <summary>
    /// A restore onto an installation configured for another embedding width takes the vector mirror
    /// with the row it mirrors.
    /// </summary>
    /// <remarks>
    /// Entered at <see cref="BackupRestoreService.RestoreAsync"/> over a real archive, because the
    /// width the drop compares against is the restore service's own option and the staged database it
    /// runs on only exists inside a restore.
    ///
    /// <para>The dropped base-table count is asserted as well as the mirror, and both matter: without
    /// the count, a mirror that is empty because nothing ran at all would satisfy the case; without the
    /// mirror, this is a restore that hands the operator two tables disagreeing about which entries
    /// have vectors, which is the state a semantic search then answers from.</para>
    /// </remarks>
    [Fact]
    public async Task A_restore_under_a_different_embedding_width_takes_the_vector_mirror_with_it()
    {

        Fixture fixture = await CreateFixtureAsync(mirroredEmbeddings: true);

        string archive = await fixture.CreateBackupAsync("embedding-width.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        // The archive's vectors are 768-wide and this installation is configured for 1536, so the drop
        // is what this restore actually did — pinned before the mirror is looked at.
        Assert.Equal(1, result.Reconciliation?.EmbeddingsToRebuild);

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            "0",
            await ScalarAsync(connection, "SELECT COUNT(*) FROM entry_embeddings;"));

        Assert.Equal(
            "0",
            await ScalarAsync(connection, "SELECT COUNT(*) FROM entry_embeddings_vec;"));

    }

    /// <summary>
    /// The JSON restore document reports the vectors a restore dropped under a name that does not
    /// claim it rebuilt them.
    /// </summary>
    /// <remarks>
    /// Asserted over the exact document <c>--output-format json</c> emits — the same
    /// <see cref="CliJsonContext"/> type info the command serializes through — because the field name
    /// is the whole contract here. The count reaching it is a DELETE count: nothing in a restore
    /// recomputes a vector, and the method that produces it says so in its own remarks. An automation
    /// reading the old <c>embeddingsRebuilt</c> concluded the restored Grimoire had that many freshly
    /// computed vectors when it has that many fewer than the archive carried.
    ///
    /// <para>The rendered text was always honest ("N embeddings to rebuild"); it was the machine-
    /// readable half that was not, and nothing in the documentation disambiguated it.</para>
    /// </remarks>
    [Fact]
    public async Task The_json_restore_document_names_dropped_vectors_without_claiming_a_rebuild()
    {

        Fixture fixture = await CreateFixtureAsync(mirroredEmbeddings: true);

        string archive = await fixture.CreateBackupAsync("embedding-width-json.arcbackup");

        WipeInstallation();

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        string document = JsonSerializer.Serialize(
            result,
            CliJsonContext.Default.BackupRestoreResult);

        using JsonDocument parsed = JsonDocument.Parse(document);

        JsonElement reconciliation = parsed.RootElement.GetProperty("reconciliation");

        Assert.Equal(1, reconciliation.GetProperty("embeddingsToRebuild").GetInt64());

        Assert.False(
            reconciliation.TryGetProperty("embeddingsRebuilt", out _),
            "The restore document still claims it rebuilt the vectors it deleted.");

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
    public async Task Replacement_restore_publishes_the_client_blocker_before_its_first_mutation_and_retires_it_last()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("client-coordinated.arcbackup");

        ClientMutationBlockerStore blocker = new(_installation);

        InstallationMaintenanceCoordination coordination = new(
            _installation,
            blocker,
            new ClearResetEvidenceProbe(),
            new BackupRestoreClientMutationEvidenceProbe(
                _installation,
                new InMemoryOsCredentialStore()));

        BackupRestoreServiceOptions options = new()
        {
            BeforeFirstRestoreMutationForTests = () =>
            {

                Assert.True(File.Exists(blocker.BlockerPath));

                Assert.Equal(
                    ArcanumClientMutationLockAcquisitionDisposition.Contended,
                    ArcanumClientMutationLock.AcquireDetailed(_installation).Disposition);

            },
        };

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore(),
                options,
                coordination: coordination)
            .RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Null((await blocker.InspectAsync()).Value);

        using ArcanumClientMutationLock released = Assert.IsType<ArcanumClientMutationLock>(
            ArcanumClientMutationLock.AcquireDetailed(_installation).Lock);

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
    /// The staging parent, the index entry, the staging root, the journal and the captured secrets are
    /// all built before the phase loop, and a failure in any of them has to arrive the way every other
    /// restore failure does: as a typed blocker with a plan and phase records attached. Escaping as an
    /// exception costs the operator everything that matters here — the reason, the path, and the one
    /// sentence that says the current installation was never touched.
    /// </summary>
    [Fact]
    public async Task A_staging_root_that_cannot_be_created_is_a_typed_refusal_rather_than_an_exception()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("unwritable-profile.arcbackup");

        string sealedParent = Path.Combine(_root, "sealed");

        Directory.CreateDirectory(sealedParent);

        File.SetUnixFileMode(sealedParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {

            if (CanCreateDirectoryIn(sealedParent))
            {

                // A process that outranks the mode — root in a container, most often — cannot observe
                // the refusal at all, so there is nothing here to assert.
                return;

            }

            BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.NewProfileRoot,
                    Path.Combine(sealedParent, "profile", "arcanum"),
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

            Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

            Assert.Contains(result.Issues, static issue => issue.Code == "backup.restore_failed");

            Assert.Equal(
                "# the archived codex",
                await File.ReadAllTextAsync(Path.Combine(_installation, "CODEX.md")));

        }
        finally
        {

            File.SetUnixFileMode(
                sealedParent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

    }

    /// <summary>
    /// A staging root the failed preparation could not delete has to be left journal-less, so the startup
    /// sweep treats it as someone else's directory rather than adopting it.
    /// </summary>
    /// <remarks>
    /// <c>BackupRestoreJournal.Discover</c> lists only staging roots that still hold a readable journal —
    /// a directory that merely looks like staging without one is not ours to touch. The long-standing
    /// cleanup in the outer <c>finally</c> therefore deletes the journal before it deletes the directory,
    /// and the preparation catch has to do the same: it is reached with the journal already written, and
    /// if the directory removal then fails, an intact Stage-phase journal is exactly what the next start
    /// picks up and resumes — for a restore that never touched the installation.
    /// </remarks>
    [Fact]
    public async Task An_undeletable_staging_root_is_left_without_a_journal_for_the_startup_sweep()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("undeletable-staging.arcbackup");

        string stagingParent = Path.GetDirectoryName(_installation)!;

        SealingSecretStore store = new(stagingParent);

        try
        {

            BackupRestoreResult result = await Restore(store).RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.ReplaceInstallation,
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

            Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

            Assert.Contains(result.Issues, static issue => issue.Code == "backup.restore_failed");

            Assert.True(store.Sealed, "The preparation never reached the post-journal capture.");

            if (!Directory.Exists(store.SealedStagingRoot!))
            {

                // A process that outranks the mode — root in a container, most often — deletes the
                // staging root anyway, and there is no surviving directory to assert about.
                return;

            }

            // The surviving directory is the whole point: it is the only state in which the journal's
            // presence still decides anything.
            Assert.Empty(BackupRestoreJournal.Discover(stagingParent));

        }
        finally
        {

            File.SetUnixFileMode(
                stagingParent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        }

    }

    /// <summary>
    /// Only a replace-installation restore rebuilds local secret protection, so it is the only mode
    /// that can adopt the archived key. Accepted anywhere else the option promises an adoption in the
    /// plan and then performs none — the one variant of this that produces no report at all.
    /// </summary>
    [Fact]
    public async Task Adopting_the_archived_master_api_key_outside_a_replacement_is_refused()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync(
            "master-key-mode.arcbackup",
            include: [BackupComponent.MasterApiKey]);

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.NewProfileRoot,
                Path.Combine(_root, "adopting-profile"),
                Confirmed: true,
                CreateSafetyBackup: false,
                RestoreMasterApiKey: true),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Contains(
            result.Issues,
            static issue => issue.Code == "backup.restore_master_api_key_not_applicable");

        Assert.False(Directory.Exists(Path.Combine(_root, "adopting-profile")));

    }

    private static bool CanCreateDirectoryIn(string parent)
    {

        try
        {

            string probe = Path.Combine(parent, "probe-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(probe);

            Directory.Delete(probe);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return false;

        }

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

    /// <summary>
    /// A selective import stopped by its third Session reports the two it already committed rather
    /// than the status that means the destination was never touched.
    /// </summary>
    /// <remarks>
    /// Entered at <see cref="BackupRestoreService.RestoreAsync"/> over a real archive and the real
    /// live installation, because the property under test is what the operator is told about a
    /// destination that has already changed — and that sentence is only assembled at this layer.
    ///
    /// <para>The refusal is a source taint, which is the one class of per-Session refusal no preflight
    /// can answer: it is read out of that Session's own graph inside the transfer store, after the
    /// Sessions before it have committed under their own compound leases with no outer transaction
    /// over them. The archive carries all three, and the selection names all three.</para>
    ///
    /// <para>The destination is queried for what landed rather than trusting the returned counts. The
    /// installation this archive was taken from is also the installation being imported into, so both
    /// committed Sessions arrive beside their originals under fresh identities — two rows for each of
    /// the titles that committed, one for the title that was refused.</para>
    /// </remarks>
    [Fact]
    public async Task A_selective_import_refused_partway_reports_the_Sessions_it_already_committed()
    {

        Fixture fixture = await CreateFixtureAsync(refusableSessionTrio: true);

        string archive = await fixture.CreateBackupAsync("import-partial.arcbackup");

        BackupRestoreResult result = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret },
                ProtectedSelectiveImport())
            .RestoreAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.ImportSelectedSessions,
                    SessionIds:
                    [
                        Fixture.CleanSessionA,
                        Fixture.CleanSessionB,
                        Fixture.TaintedSessionC,
                    ],
                    Confirmed: true,
                    CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        // The refusal this case is about, pinned before anything else is asserted: a coverage or digest
        // refusal would stop the import somewhere else entirely and prove nothing about a partial commit.
        Assert.Contains(
            result.Issues,
            static issue => issue.Message.Contains(
                "Covenant-derived artifacts",
                StringComparison.Ordinal));

        Assert.NotEqual(BackupRestoreStatus.Rejected, result.Status);

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);

        BackupVerifyIssue committed = Assert.Single(
            result.Issues,
            static issue => issue.Code == "backup.restore_import_partially_committed");

        Assert.Contains(
            Fixture.CleanSessionA.ToString("D"),
            committed.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            Fixture.CleanSessionB.ToString("D"),
            committed.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            Fixture.TaintedSessionC.ToString("D"),
            committed.Message,
            StringComparison.OrdinalIgnoreCase);

        await using SqliteConnection connection = await OpenRestoredAsync(fixture.GrimoireSecret);

        Assert.Equal(
            "2",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM \"Sessions\" WHERE \"Title\" = 'Session A';"));

        Assert.Equal(
            "2",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM \"Sessions\" WHERE \"Title\" = 'Session B';"));

        Assert.Equal(
            "1",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM \"Sessions\" WHERE \"Title\" = 'Session C';"));

        // The graph, not just the Session row: an import that committed an empty Session would satisfy
        // every count above.
        Assert.Equal(
            "2",
            await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM \"Entries\" WHERE \"Content\" = 'ask A';"));

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

        BackupVerifyIssue issue = Assert.Single(
            result.Issues,
            static candidate => candidate.Code == "backup.restore_maintenance_unavailable");

        Assert.Contains("another process", issue.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("installation reset", issue.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Unsafe_maintenance_lock_topology_is_reported_truthfully_without_staging_or_mutation()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("unsafe-lock.arcbackup");

        string lockPath = ArcanumMaintenanceLock.LockPathFor(_installation);

        string sentinel = Path.Combine(_root, "unsafe-lock-sentinel.txt");

        byte[] original = "unsafe-lock-target"u8.ToArray();

        File.WriteAllBytes(sentinel, original);

        File.CreateSymbolicLink(lockPath, sentinel);

        BackupRestoreResult result = await Restore(new RecordingSecretStore()).RestoreAsync(
            new BackupRestoreRequest(archive, Confirmed: true, CreateSafetyBackup: false),
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(
            result.Issues,
            static candidate => candidate.Code == "backup.restore_maintenance_unavailable");

        Assert.DoesNotContain("another process", issue.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("safely", issue.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(original, File.ReadAllBytes(sentinel));

        Assert.Empty(
            Directory.GetDirectories(
                Path.GetDirectoryName(_installation)!,
                ".arcanum-restore-*",
                SearchOption.TopDirectoryOnly));

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

    [Fact]
    public async Task A_Campaign_mapping_naming_a_Campaign_this_machine_does_not_have_is_a_plan_blocker()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-mapping.arcbackup");

        Guid absent = Guid.Parse("77777777-7777-7777-7777-777777777777");

        BackupRestorePlan plan = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret },
                SelectiveImportEnabled())
            .PlanAsync(
                ImportWithMapping(archive, absent),
                Passphrase.AsMemory(),
                CancellationToken.None);

        BackupVerifyIssue blocker = Assert.Single(
            plan.Blockers,
            static issue => issue.Code == BackupRestoreCampaignMappingPolicy.DestinationMissingCode);

        // Named, because "no such Campaign" without saying which one leaves an operator re-reading
        // their own command line to work out which half of which mapping was wrong.
        Assert.Contains(absent.ToString("D"), blocker.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_Campaign_mapping_naming_a_Campaign_this_machine_has_is_planned_without_a_blocker()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-mapping-ok.arcbackup");

        // Named in the lowercase form a Guid renders by default, while the row was seeded in the
        // uppercase "D"-format text EF actually writes. SQLite compares TEXT byte for byte, so a
        // `WHERE "Id" = $id` probe would match nothing here and refuse a mapping that is correct.
        Assert.NotEqual(
            Fixture.LetteredCampaignId.ToString("D"),
            Fixture.LetteredCampaignId.ToString("D").ToUpperInvariant());

        BackupRestorePlan plan = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret },
                SelectiveImportEnabled())
            .PlanAsync(
                ImportWithMapping(archive, Fixture.LetteredCampaignId),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.DoesNotContain(
            plan.Blockers,
            static issue =>
                issue.Code == BackupRestoreCampaignMappingPolicy.DestinationMissingCode);

    }

    [Fact]
    public async Task A_destination_this_machine_cannot_read_refuses_no_Campaign_mapping()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-mapping-unreadable.arcbackup");

        // No Grimoire secret: the live installation cannot be opened, so it has nothing to say about
        // its Campaigns. "Could not be asked" must not become "that Campaign does not exist here" —
        // the import already names an unreadable destination for what it is.
        BackupRestorePlan plan = await Restore(new RecordingSecretStore(), SelectiveImportEnabled())
            .PlanAsync(
                ImportWithMapping(archive, Guid.Parse("77777777-7777-7777-7777-777777777777")),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.DoesNotContain(
            plan.Blockers,
            static issue => issue.Code.StartsWith(
                "backup.restore_campaign_mapping",
                StringComparison.Ordinal));

    }

    [Fact]
    public async Task A_Campaign_mapping_on_a_restore_that_imports_nothing_is_refused_as_inapplicable()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-mapping-mode.arcbackup");

        BackupRestorePlan plan = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret },
                SelectiveImportEnabled())
            .PlanAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.ReplaceInstallation,
                    CampaignMappings:
                    [
                        new BackupSessionCampaignMapping(
                            Guid.Parse("66666666-6666-6666-6666-666666666666"),
                            Fixture.CampaignId),
                    ]),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Contains(
            plan.Blockers,
            static issue =>
                issue.Code == BackupRestoreCampaignMappingPolicy.NotApplicableCode);

    }

    [Fact]
    public async Task A_Campaign_mapping_without_the_Covenant_import_arm_is_refused_rather_than_ignored()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-mapping-gate-off.arcbackup");

        // No SelectiveImport services: this is the pre-Covenant import, which writes every Session's
        // CampaignId as NULL. Accepting the mapping here would hand the operator the silently unbound
        // Session the option exists to prevent.
        BackupRestorePlan plan = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret })
            .PlanAsync(
                ImportWithMapping(archive, Fixture.CampaignId),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Contains(
            plan.Blockers,
            static issue =>
                issue.Code == BackupRestoreCampaignMappingPolicy.CovenantRequiredCode);

    }

    [Fact]
    public async Task A_selective_import_that_names_no_mapping_is_untouched_by_the_arm_being_absent()
    {

        Fixture fixture = await CreateFixtureAsync();

        string archive = await fixture.CreateBackupAsync("import-no-mapping.arcbackup");

        BackupRestorePlan plan = await Restore(
                new RecordingSecretStore { GrimoireSecret = fixture.GrimoireSecret })
            .PlanAsync(
                new BackupRestoreRequest(
                    archive,
                    BackupRestoreConflictMode.ImportSelectedSessions,
                    SessionIds: [Fixture.SessionId]),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.DoesNotContain(
            plan.Blockers,
            static issue => issue.Code.StartsWith(
                "backup.restore_campaign_mapping",
                StringComparison.Ordinal));

    }

    [Fact]
    public async Task A_blocked_plan_never_opens_the_live_Grimoire_to_check_a_Campaign_mapping()
    {

        Fixture fixture = await CreateFixtureAsync();

        _ = await fixture.CreateBackupAsync("import-mapping-blocked.arcbackup");

        string missing = Path.Combine(_archives, "there-is-no-such-archive.arcbackup");

        RecordingSecretStore withoutMapping = new() { GrimoireSecret = fixture.GrimoireSecret };

        _ = await Restore(withoutMapping, SelectiveImportEnabled())
            .PlanAsync(
                new BackupRestoreRequest(
                    missing,
                    BackupRestoreConflictMode.ImportSelectedSessions,
                    SessionIds: [Fixture.SessionId]),
                Passphrase.AsMemory(),
                CancellationToken.None);

        RecordingSecretStore withMapping = new() { GrimoireSecret = fixture.GrimoireSecret };

        // Already blocked on a missing archive. Deriving the Grimoire key and opening the live
        // database to answer a question this plan has already refused is exactly the touch the
        // maintenance lock exists to keep away from a running host.
        BackupRestorePlan plan = await Restore(withMapping, SelectiveImportEnabled())
            .PlanAsync(
                ImportWithMapping(missing, Fixture.CampaignId),
                Passphrase.AsMemory(),
                CancellationToken.None);

        Assert.Contains(
            plan.Blockers,
            static issue => issue.Code == "backup.restore_archive_missing");

        Assert.DoesNotContain(
            plan.Blockers,
            static issue => issue.Code.StartsWith(
                "backup.restore_campaign_mapping",
                StringComparison.Ordinal));

        // Calibrated against the same blocked plan without a mapping rather than against a fixed
        // number, so the assertion stays about the mapping read and not about how many other readers
        // this plan happens to have.
        Assert.Equal(withoutMapping.GrimoireSecretReads, withMapping.GrimoireSecretReads);

    }

    /// <summary>
    /// The selective-import arm as a Covenant-enabled installation composes it: the real transfer
    /// store, and a gate that grants the compound lease rather than arbitrating it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SelectiveImportEnabled"/>, whose store throws: that one exists for the
    /// planning cases, where presence of the arm is all that is read. A case about what the import
    /// commits has to run the store that commits it.
    /// </remarks>
    private static BackupRestoreServiceOptions ProtectedSelectiveImport() =>
        new()
        {

            SelectiveImport = new CovenantSelectiveImportServices(
                new BackupSessionImporterTests.ProtectedTransferGate(),
                new ProtectedArtifactTransferStore(
                    CovenantSqliteConnectionInitializer.Instance,
                    TimeProvider.System)),

        };

    private static BackupRestoreServiceOptions SelectiveImportEnabled() =>
        new()
        {

            // Only presence is read on the planning path — nothing here is invoked — but it is the one
            // thing that decides whether a Campaign mapping can be honoured at all.
            SelectiveImport = new CovenantSelectiveImportServices(
                new CovenantRestoreStagingTests.RecordingExclusiveGate(),
                new UnreachableTransferStore()),

        };

    private static BackupRestoreRequest ImportWithMapping(
        string archivePath,
        Guid destinationCampaignId) =>
        new(
            archivePath,
            BackupRestoreConflictMode.ImportSelectedSessions,
            SessionIds: [Fixture.SessionId],
            CampaignMappings:
            [
                new BackupSessionCampaignMapping(
                    Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    destinationCampaignId),
            ]);

    private sealed class UnreachableTransferStore : IProtectedArtifactTransferStore
    {

        public Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>>
            CommitImportedSessionAsync(
                ImportedSessionTransferRequest request,
                ImportedSessionSourceLease sourceLease,
                CovenantProtectedTransferLease transferLease,
                ProtectedSessionImportDestination destination,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore plan commits no protected transfer.");

    }

    private BackupRestoreService Restore(
        ISecretStore secretStore,
        BackupRestoreServiceOptions? options = null,
        IBackupService? safetyBackups = null,
        InstallationMaintenanceCoordination? coordination = null) =>
        new(
            Paths(),
            Codec(),
            secretStore,
            safetyBackups is null ? null : () => safetyBackups,
            TimeProvider.System,
            GrimoireSchemaTestInstaller.Create(),
            options ?? new BackupRestoreServiceOptions(),
            coordination);

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

    private async Task<Fixture> CreateFixtureAsync(
        bool listenAny = false,
        bool refusableSessionTrio = false,
        bool mirroredEmbeddings = false,
        bool caseCollidingAttachment = false)
    {

        Fixture fixture = new(_installation, _archives, Paths(), Codec());

        await fixture.BuildAsync(
            listenAny,
            refusableSessionTrio,
            mirroredEmbeddings,
            caseCollidingAttachment);

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

        /// <summary>
        /// The three Sessions a selective import selects together, in the order the plan sorts them.
        /// </summary>
        /// <remarks>
        /// <see cref="BackupRestoreService"/> orders the selection (<c>Distinct().Order()</c>), so the
        /// one that is refused has to sort last by <see cref="Guid"/> comparison rather than by the
        /// order the request happens to list — otherwise the refusal lands before anything committed
        /// and the case proves nothing. The leading field is what that comparison reads first, and
        /// these three increase in it. Hex letters throughout, so an identity spelled uppercase in the
        /// archive and lowercase in the request is a difference this fixture can actually see.
        /// </remarks>
        public static readonly Guid CleanSessionA =
            Guid.Parse("1a1a1a1a-2b2b-4c3c-8d4d-5e5e6f6f7070");

        public static readonly Guid CleanSessionB =
            Guid.Parse("2b2b2b2b-3c3c-4d4d-8e5e-6f6f70708181");

        public static readonly Guid TaintedSessionC =
            Guid.Parse("3c3c3c3c-4d4d-4e5e-8f6f-707081819292");

        public static readonly Guid CampaignId =
            Guid.Parse("22222222-2222-2222-2222-222222222222");

        /// <summary>
        /// A second Campaign whose identity actually has letters in it, stored the way EF stores one.
        /// </summary>
        /// <remarks>
        /// <see cref="CampaignId"/> is all digits, so its uppercase and lowercase renderings are the
        /// same bytes and it can prove nothing about case. This row is seeded in the uppercase
        /// "D"-format text EF's SQLite provider actually writes, while the mapping under test names it
        /// in the lowercase form <see cref="Guid"/> renders by default — which is exactly the pair a
        /// <c>WHERE "Id" = $id</c> probe would fail to match under SQLite's BINARY collation.
        /// </remarks>
        public static readonly Guid LetteredCampaignId =
            Guid.Parse("abcdef12-3456-4789-abcd-ef1234567890");

        /// <summary>
        /// The installation's suppression key material, and one digest bound by it, as stored hex.
        /// </summary>
        /// <remarks>
        /// Fixed values rather than random ones, because the property under test is that the exact bytes
        /// arrive. A digest whose key did not travel with it, or a key whose digests did not, is
        /// evidence of nothing — so the pair is asserted together and by content.
        /// </remarks>
        public const string SuppressionKeyMaterialHex =
            "0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20";

        public const string SuppressionDigestHex =
            "A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF";

        public string GrimoireSecret { get; } = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        public string DatabasePath => Path.Combine(installation, "arcanum.db");

        public IBackupService BackupService { get; private set; } = null!;

        public async Task BuildAsync(
            bool listenAny,
            bool refusableSessionTrio = false,
            bool mirroredEmbeddings = false,
            bool caseCollidingAttachment = false)
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

            if (refusableSessionTrio)
            {

                await SeedRefusableSessionTrioAsync();

            }

            if (mirroredEmbeddings)
            {

                await SeedMirroredEmbeddingAsync();

            }

            if (caseCollidingAttachment)
            {

                await SeedCaseCollidingAttachmentAsync();

            }

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

            await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
                new SqliteConnectionStringBuilder
                {

                    DataSource = DatabasePath,

                    Password = passphrase,

                    Pooling = false,

                }.ToString(),
                CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = """
                INSERT INTO "Campaigns"
                    ("Id", "Name", "NameLower", "Path", "Type", "Description", "Settings",
                     "SanctumConfigJson", "CreatedAt", "UpdatedAt")
                VALUES ('22222222-2222-2222-2222-222222222222', 'Alpha', 'alpha',
                        'C:\Users\Old\campaigns\alpha', 0, NULL, '{}',
                        '{"allowedPaths":["C:\\Users\\Old\\src\\project"]}',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Campaigns"
                    ("Id", "Name", "NameLower", "Path", "Type", "Description", "Settings",
                     "SanctumConfigJson", "CreatedAt", "UpdatedAt")
                VALUES ('ABCDEF12-3456-4789-ABCD-EF1234567890', 'Beta', 'beta',
                        'C:\Users\Old\campaigns\beta', 0, NULL, '{}', '{}',
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

            await using SqliteCommand curation = connection.CreateCommand();

            // Seeded rather than retired into existence: what this fixture is for is transport, and the
            // writers that create these two rows are proved against the store's own suite.
            curation.CommandText = $"""
                INSERT INTO saga_suppression_key (KeyId, KeyMaterial, CreatedAtUtc)
                VALUES (1, X'{SuppressionKeyMaterialHex}', '2026-01-01T00:00:00Z');

                INSERT INTO saga_retirement_suppressions
                    (SuppressionDigest, ScopeKindCode, CampaignId, RetiredAtUtc)
                VALUES (X'{SuppressionDigestHex}', 2, '{CampaignId:D}', '2026-01-01T00:00:00Z');
                """;

            _ = await curation.ExecuteNonQueryAsync();

        }

        /// <summary>
        /// Three more Sessions, of which the last carries a Covenant sensitivity label.
        /// </summary>
        /// <remarks>
        /// Seeded into the installation the archive is taken from, so all three travel inside a real
        /// archive rather than being handed to the importer directly. The label is what the protected
        /// transfer store's own taint scan reads, and it is the refusal a selective import cannot
        /// preflight: it is discovered while reading that Session's graph, after the Sessions before it
        /// have already committed under their own leases.
        ///
        /// <para>The identities are spelled the way the object-relational writer spells one — uppercase
        /// dashed — because that is what an ordinary archive holds and what the schema's identity guards
        /// admit.</para>
        /// </remarks>
        private async Task SeedRefusableSessionTrioAsync()
        {

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
                DatabasePath,
                GrimoireSecret,
                readOnly: false,
                CancellationToken.None);

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{Upper(CleanSessionA)}', NULL, 'Session A', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{Upper(CleanSessionB)}', NULL, 'Session B', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt")
                VALUES ('{Upper(TaintedSessionC)}', NULL, 'Session C', 'active',
                        '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

                INSERT INTO "Entries"
                    ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ('{Upper(EntryOfA)}', '{Upper(CleanSessionA)}', 0, 'ask A', 'test',
                        '2026-01-01T00:00:00Z', 1);

                INSERT INTO "Entries"
                    ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ('{Upper(EntryOfB)}', '{Upper(CleanSessionB)}', 0, 'ask B', 'test',
                        '2026-01-01T00:00:00Z', 1);

                INSERT INTO "Entries"
                    ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
                VALUES ('{Upper(EntryOfC)}', '{Upper(TaintedSessionC)}', 0, 'ask C', 'test',
                        '2026-01-01T00:00:00Z', 1);

                INSERT INTO artifact_sensitivity
                    ("LabelId", "ArtifactKindCode", "ArtifactId", "SensitivityCode",
                     "ProvenanceModeCode", "ExactGenerationIds", "GenerationBloom", "SessionId",
                     "CampaignId", "TurnId", "ArtifactRevision", "ArtifactContentDigest",
                     "SensitivityDigest", "ProducingPlanDigest", "ProducingAdmissionDigest",
                     "ProducingMaintenanceReceiptDigest", "ArtifactLabelDigest", "CreatedAtUtc")
                VALUES ('{Upper(Guid.Parse("4d4d4d4d-5e5e-4f6f-8070-81819292a3a3"))}', 1,
                        '{Upper(EntryOfC)}', 1, 1,
                        X'000102030405060708090A0B0C0D0E0F', NULL, '{Upper(TaintedSessionC)}',
                        NULL, NULL, 0,
                        X'{new string('1', 64)}', X'{new string('2', 64)}', NULL, NULL, NULL,
                        X'{new string('3', 64)}', '2026-01-01T00:00:00Z');
                """;

            _ = await seed.ExecuteNonQueryAsync();

        }

        /// <summary>
        /// A derived vector at a width this installation is not configured for, and its mirror row.
        /// </summary>
        /// <remarks>
        /// The mirror is an ordinary table here rather than a <c>vec0</c> virtual one, because the
        /// accelerator is not loadable in this suite — the same stand-in the retention suites use. What
        /// is under test is which tables a restore reconciles, and that is decided by name and key
        /// column rather than by whether the accelerator is present: production guards every mirror
        /// statement with an existence check for exactly that reason.
        ///
        /// <para>Seeded into the installation the archive is taken from, so the width mismatch arrives
        /// through a real archive: the database snapshot is a page copy of the whole encrypted file, so
        /// a table joins a backup by existing.</para>
        /// </remarks>
        private async Task SeedMirroredEmbeddingAsync()
        {

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
                DatabasePath,
                GrimoireSecret,
                readOnly: false,
                CancellationToken.None);

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = $"""
                INSERT INTO entry_embeddings ("EntryId", "Embedding", "Dim")
                VALUES ('{Upper(MirroredEntryId)}', X'0000803F', 768);

                CREATE TABLE IF NOT EXISTS entry_embeddings_vec (
                    EntryId TEXT PRIMARY KEY,
                    Embedding BLOB NOT NULL
                );

                INSERT INTO entry_embeddings_vec ("EntryId", "Embedding")
                VALUES ('{Upper(MirroredEntryId)}', X'0000803F');
                """;

            _ = await seed.ExecuteNonQueryAsync();

        }

        /// <summary>
        /// A second attachment whose recorded path differs from the first only in case.
        /// </summary>
        /// <remarks>
        /// Both paths come out of the database and go into the archive as the writer found them: an
        /// attachment's archive path is <c>"attachments/" + RelativePath</c>, the stored relative path
        /// carries a user-supplied logical key and file name, and the sanitizer that admits one strips
        /// characters without ever changing case. The archive writer's own duplicate check is ordinal,
        /// so both entries are written.
        ///
        /// <para>What the two entries contain depends on the volume this suite runs on, and neither
        /// case is the point: on a case-insensitive filesystem there is one physical file and the two
        /// rows name it twice; on a case-sensitive one there are two files holding different bytes. The
        /// archive carries two entries whose paths collide under case folding either way, which is the
        /// only thing the extraction has to answer for.</para>
        /// </remarks>
        private async Task SeedCaseCollidingAttachmentAsync()
        {

            string colliding = Path.Combine(installation, "attachments", "session", "NOTE.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(colliding)!);

            await File.WriteAllTextAsync(colliding, "the other attachment");

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(
                DatabasePath,
                GrimoireSecret,
                readOnly: false,
                CancellationToken.None);

            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = """
                INSERT INTO "SessionAttachments"
                    ("Id", "SessionId", "State", "LogicalKey", "OriginalFileName", "Version",
                     "RelativePath", "ContentSha256", "MimeType", "ByteLength", "Kind", "CreatedAt",
                     "SourceKind", "SourceCanonicalPath", "SourceStatus", "EncryptionVersion")
                VALUES ('66666666-6666-4666-8666-666666666666',
                        '11111111-1111-1111-1111-111111111111', 'Bound', 'NOTE', 'NOTE.txt', 1,
                        'session/NOTE.bin', 'def', 'text/plain', 20, 'Text',
                        '2026-01-01T00:00:00Z', 'WorkspaceFile',
                        'C:\Users\Old\src\project\NOTE.txt', 'Refreshable', 0);
                """;

            _ = await seed.ExecuteNonQueryAsync();

        }

        public static readonly Guid MirroredEntryId =
            Guid.Parse("5e5e5e5e-6f6f-4070-8181-9292a3a3b4b4");

        public static readonly Guid EntryOfA = Guid.Parse("a1a1a1a1-b2b2-4c3c-8d4d-5e5e6f6f7071");

        public static readonly Guid EntryOfB = Guid.Parse("b2b2b2b2-c3c3-4d4d-8e5e-6f6f70708182");

        public static readonly Guid EntryOfC = Guid.Parse("c3c3c3c3-d4d4-4e5e-8f6f-707081819293");

        private static string Upper(Guid value) => value.ToString("D").ToUpperInvariant();

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

    /// <summary>
    /// Fails the secret capture at the one moment the staging journal exists and the staging root does
    /// not yet, and makes that root undeletable on the way out.
    /// </summary>
    /// <remarks>
    /// <c>BackupSecretRewrapper.CaptureAsync</c> is the last step of staging preparation, and the
    /// Grimoire read is its first call — so a throw here lands in the preparation catch with the journal
    /// already on disk. Sealing the staging <em>parent</em> read-only is what makes the directory removal
    /// fail while the journal deletion inside it still succeeds, which is precisely the asymmetric window
    /// under test. The parent is sealed rather than the staging root itself because removing a directory
    /// needs write permission on its container, and deleting the journal needs it on the staging root.
    /// </remarks>
    private sealed class SealingSecretStore(string stagingParent) : ISecretStore
    {

        public bool Sealed { get; private set; }

        public string? SealedStagingRoot { get; private set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync()
        {

            IReadOnlyList<string> staged = BackupRestoreJournal.Discover(stagingParent);

            if (staged.Count == 0)
            {

                // Earlier reads run before the journal exists; those must succeed or the restore never
                // reaches the window this double exists to open.
                return Task.FromResult<string?>("the current machine secret");

            }

            SealedStagingRoot = staged[0];

            Sealed = true;

            if (!OperatingSystem.IsWindows())
            {

                File.SetUnixFileMode(stagingParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            }

            throw new IOException("The staging parent became unwritable mid-preparation.");

        }

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

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

        /// <summary>How many times a caller asked for the secret that opens the live Grimoire.</summary>
        public int GrimoireSecretReads { get; private set; }

        public Task<string?> GetGrimoireEncryptionSecretAsync()
        {

            GrimoireSecretReads++;

            return Task.FromResult(GrimoireSecret);

        }

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

    private sealed class ClearResetEvidenceProbe :
        IClientMutationResetEvidenceProbe
    {

        public Task<Result<ActiveInstallationReset?>> InspectAsync(
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Result<ActiveInstallationReset?>.Success(null));

        }

    }

}
