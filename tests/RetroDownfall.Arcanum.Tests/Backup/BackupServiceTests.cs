using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupServiceTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-service-" + Guid.NewGuid().ToString("N"));

    public BackupServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Metadata_only_create_inspect_verify_and_list_are_secret_free_and_do_not_need_installation_state()
    {

        BackupStatePaths paths = Paths();

        CountingSecretReader secrets = new();

        BackupService service = CreateService(paths, secrets);

        string archive = Path.Combine(_root, "metadata.arcbackup");

        BackupPlanRequest plan = new(
            BackupScope.MetadataOnly,
            SessionId: null,
            Include: [],
            Exclude: []);

        BackupCreateResult created = await service.CreateAsync(
            new BackupCreateRequest(plan, archive, Overwrite: false),
            "metadata passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        Assert.Equal(archive, created.ArchivePath);

        Assert.True(File.Exists(archive));

        Assert.Equal(0, secrets.ReadCount);

        BackupInspectResult outer = await service.InspectAsync(
            archive,
            recoveryPassphrase: null,
            CancellationToken.None);

        Assert.Null(outer.Manifest);

        BackupInspectResult inner = await service.InspectAsync(
            archive,
            "metadata passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupScope.MetadataOnly, inner.Manifest?.Scope);

        Assert.Empty(inner.Manifest?.Entries ?? []);

        BackupVerifyResult verified = await service.VerifyAsync(
            archive,
            "metadata passphrase".AsMemory(),
            CancellationToken.None);

        Assert.True(
            verified.IsValid,
            string.Join(", ", verified.Issues.Select(static issue => issue.Code)));

        IReadOnlyList<BackupListItem> listed = await service.ListAsync(
            _root,
            CancellationToken.None);

        Assert.Contains(listed, item => item.ArchivePath == archive);

        string maliciousArchive = Path.Combine(_root, "huge-length.arcbackup");

        byte[] malicious = await File.ReadAllBytesAsync(archive);

        BinaryPrimitives.WriteInt64BigEndian(
            malicious.AsSpan(28, sizeof(long)),
            long.MaxValue);

        await File.WriteAllBytesAsync(maliciousArchive, malicious);

        listed = await service.ListAsync(
            _root,
            CancellationToken.None);

        Assert.Contains(listed, item => item.ArchivePath == archive);

        Assert.DoesNotContain(
            listed,
            item => item.ArchivePath == maliciousArchive);

    }

    [Fact]
    public async Task Metadata_configuration_create_uses_service_owned_stage_for_self_verification_scratch()
    {

        BackupStatePaths paths = Paths();

        string configurationPath = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.json");

        await File.WriteAllTextAsync(configurationPath, "{}");

        string presetStatePath = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.preset.json");

        string presetRollbackPath = Path.Combine(
            paths.GrimoireDirectory,
            "arcanum.preset.rollback.json");

        await File.WriteAllTextAsync(
            presetStatePath,
            "{\"presetId\":\"research\"}");

        await File.WriteAllTextAsync(
            presetRollbackPath,
            "{\"presetId\":\"research\"}");

        List<string> payloadPaths = [];

        List<string> extractionPaths = [];

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            ChunkSize = 64 * 1024,

            BeforeTemporaryPayloadCleanupForTests = path =>
                payloadPaths.Add(path),

            BeforeTemporaryExtractionCleanupForTests = path =>
                extractionPaths.Add(path),

        });

        BackupService service = new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            codec,
            new CountingSecretReader(),
            TimeProvider.System);

        string archive = Path.Combine(
            _root,
            "metadata-configuration.arcbackup");

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(
                    BackupScope.MetadataOnly,
                    SessionId: null,
                    Include: [BackupComponent.Configuration],
                    Exclude: []),
                archive,
                Overwrite: false),
            "scratch passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, result.Status);

        Assert.Equal(
            [
                "configuration/arcanum.json",
                "configuration/arcanum.preset.json",
                "configuration/arcanum.preset.rollback.json",
            ],
            result.Manifest?.Entries.Select(static entry => entry.Path));

        string payloadPath = Path.GetFullPath(
            Assert.Single(payloadPaths));

        string extractionPath = Path.GetFullPath(
            Assert.Single(extractionPaths));

        string stageRoot = Assert.IsType<string>(
            Path.GetDirectoryName(payloadPath));

        Assert.StartsWith(
            ".arcanum-backup-stage-",
            Path.GetFileName(stageRoot),
            StringComparison.Ordinal);

        Assert.Equal(
            stageRoot,
            Path.GetDirectoryName(extractionPath));

        Assert.False(Directory.Exists(stageRoot));

        Assert.Empty(
            Directory.GetDirectories(
                _root,
                ".arcanum-backup-stage-*",
                SearchOption.TopDirectoryOnly));

        Assert.True(File.Exists(archive));

        Assert.True(File.Exists(configurationPath));

        Assert.True(File.Exists(presetStatePath));

        Assert.True(File.Exists(presetRollbackPath));

    }

    [Fact]
    public async Task CreateAsync_CanonicalizesRequestedOverridesInTheManifest()
    {

        BackupService service = CreateService(
            Paths(),
            new CountingSecretReader());

        string archive = Path.Combine(_root, "canonical-overrides.arcbackup");

        BackupPlanRequest plan = new(
            BackupScope.MetadataOnly,
            SessionId: null,
            Include:
            [
                BackupComponent.CompendiumSettings,
                BackupComponent.Configuration,
                BackupComponent.CompendiumSettings,
            ],
            Exclude:
            [
                BackupComponent.MasterApiKey,
                BackupComponent.AuditLogs,
                BackupComponent.MasterApiKey,
            ]);

        BackupCreateResult created = await service.CreateAsync(
            new BackupCreateRequest(plan, archive, Overwrite: false),
            "canonical passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        BackupInspectResult inspected = await service.InspectAsync(
            archive,
            "canonical passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(
            [
                BackupComponent.Configuration,
                BackupComponent.CompendiumSettings,
            ],
            inspected.Manifest?.RequestedIncludes);

        Assert.Equal(
            [
                BackupComponent.AuditLogs,
                BackupComponent.MasterApiKey,
            ],
            inspected.Manifest?.RequestedExcludes);

    }

    [Fact]
    public async Task CreateAsync_PreservesPermissionsOfAnExistingOutputParentOnUnix()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string outputParent = Path.Combine(_root, "shared-output");

        Directory.CreateDirectory(outputParent);

        UnixFileMode sharedMode =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute;

        File.SetUnixFileMode(outputParent, sharedMode);

        BackupService service = CreateService(
            Paths(),
            new CountingSecretReader());

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(
                    BackupScope.MetadataOnly,
                    SessionId: null,
                    Include: [],
                    Exclude: []),
                Path.Combine(outputParent, "portable.arcbackup"),
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, result.Status);

        Assert.Equal(sharedMode, File.GetUnixFileMode(outputParent));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(result.ArchivePath!));

    }

    [Fact]
    public async Task CreateAsync_CreatesAMissingOutputParent()
    {

        string outputParent = Path.Combine(_root, "new-output", "nested");

        string archive = Path.Combine(outputParent, "portable.arcbackup");

        BackupService service = CreateService(
            Paths(),
            new CountingSecretReader());

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(
                    BackupScope.MetadataOnly,
                    SessionId: null,
                    Include: [],
                    Exclude: []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, result.Status);

        Assert.True(Directory.Exists(outputParent));

        Assert.True(File.Exists(archive));

    }

    [Fact]
    public async Task Failed_inventory_never_publishes_an_archive()
    {

        BackupStatePaths paths = Paths();

        Directory.CreateDirectory(paths.AttachmentsDirectory);

        const string grimoireSecret = "grimoire-secret";

        await CreateMissingAttachmentDatabaseAsync(paths.DatabasePath, grimoireSecret);

        BackupService service = CreateService(
            paths,
            new CountingSecretReader(
                SecretStoreReadResult.Ok(grimoireSecret)));

        string archive = Path.Combine(_root, "incomplete.arcbackup");

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, null, [], []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Incomplete, result.Status);

        Assert.Null(result.ArchivePath);

        Assert.False(File.Exists(archive));

        Assert.Contains(
            result.Plan.Components,
            component => component.Component == BackupComponent.SessionAttachments
                && component.Status == BackupComponentStatus.Failed);

    }

    [Fact]
    public async Task Database_backup_reports_durable_progress_and_completes_the_same_operation()
    {

        BackupStatePaths paths = Paths();

        const string grimoireSecret = "grimoire-secret";

        await CreateMissingAttachmentDatabaseAsync(paths.DatabasePath, grimoireSecret);

        await using (Microsoft.Data.Sqlite.SqliteConnection connection = new(
                         new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                         {

                             DataSource = paths.DatabasePath,

                             Password = DeriveDatabasePassphrase(paths.DatabasePath, grimoireSecret),

                             Pooling = false,

                         }.ToString()))
        {

            await connection.OpenAsync();

            await using Microsoft.Data.Sqlite.SqliteCommand remove = connection.CreateCommand();

            remove.CommandText = "DELETE FROM SessionAttachments;";

            _ = await remove.ExecuteNonQueryAsync();

        }

        RecordingOperationJournal operations = new();

        BackupService service = new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            new BackupArchiveCodec(new BackupArchiveCodecOptions
            {

                KdfIterations = 10_000,

                ChunkSize = 64 * 1024,

            }),
            new CountingSecretReader(SecretStoreReadResult.Ok(grimoireSecret)),
            TimeProvider.System,
            passphraseSource: null,
            operations,
            operations);

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.SessionsAndMemory, null, [], []),
                Path.Combine(_root, "durable.arcbackup"),
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, result.Status);

        Assert.Equal(operations.OperationId, result.OperationId);

        Assert.Equal(LongRunningOperationKinds.BackupCreate, operations.CreatedKind);

        Assert.True(operations.CheckpointCount >= 2);

        Assert.All(
            operations.CheckpointVersions,
            static version => Assert.Equal(2, version));

        BackupOperationCheckpoint[] checkpoints = operations.CheckpointPayloads
            .Select(payload => JsonSerializer.Deserialize(
                payload,
                BackupJsonContext.Default.BackupOperationCheckpoint)!)
            .ToArray();

        Assert.Contains(
            checkpoints,
            static checkpoint => checkpoint.Phase == "staging-planned"
                && !checkpoint.StagingVolumeId.HasValue
                && !checkpoint.StagingFileId.HasValue);

        Assert.Contains(
            checkpoints,
            static checkpoint => checkpoint.Phase == "staging-created"
                && checkpoint.StagingVolumeId.HasValue
                && checkpoint.StagingFileId.HasValue);

        BackupOperationCheckpoint published = Assert.Single(
            checkpoints,
            static checkpoint => checkpoint.Phase == "archive-published");

        Assert.False(Directory.Exists(published.StagingRoot));

        Assert.True(operations.Completed);

        Assert.False(operations.Failed);

    }

    [Fact]
    public async Task Source_replaced_after_inventory_is_rejected_without_publishing_an_archive()
    {

        BackupStatePaths paths = Paths();

        string configurationPath = Path.Combine(paths.GrimoireDirectory, "arcanum.json");

        string originalPath = configurationPath + ".original";

        await File.WriteAllTextAsync(configurationPath, "{\"a\":1}");

        bool replaced = false;

        BackupService service = CreateService(
            paths,
            new CountingSecretReader(),
            _ =>
            {

                if (replaced)
                {

                    return;

                }

                replaced = true;

                File.Move(configurationPath, originalPath);

                File.WriteAllText(configurationPath, "{\"b\":2}");

            });

        string archive = Path.Combine(_root, "replaced.arcbackup");

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(
                    BackupScope.ConfigurationAndAuthoredAssets,
                    SessionId: null,
                    Include: [],
                    Exclude: []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None));

        Assert.False(File.Exists(archive));

        Assert.Equal("{\"b\":2}", await File.ReadAllTextAsync(configurationPath));

        Assert.Equal("{\"a\":1}", await File.ReadAllTextAsync(originalPath));

    }

    [Fact]
    public async Task Full_backup_requires_the_active_file_encryption_key_without_blob_references()
    {

        BackupStatePaths paths = Paths();

        const string grimoireSecret = "grimoire-secret";

        await CreateMissingAttachmentDatabaseAsync(paths.DatabasePath, grimoireSecret);

        await using (Microsoft.Data.Sqlite.SqliteConnection connection = new(
                         new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                         {

                             DataSource = paths.DatabasePath,

                             Password = DeriveDatabasePassphrase(paths.DatabasePath, grimoireSecret),

                             Pooling = false,

                         }.ToString()))
        {

            await connection.OpenAsync();

            await using Microsoft.Data.Sqlite.SqliteCommand remove = connection.CreateCommand();

            remove.CommandText = "DELETE FROM SessionAttachments;";

            _ = await remove.ExecuteNonQueryAsync();

        }

        BackupService service = CreateService(
            paths,
            new CountingSecretReader(
                SecretStoreReadResult.Ok(grimoireSecret)));

        string archive = Path.Combine(_root, "missing-active-key.arcbackup");

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, null, [], []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Incomplete, result.Status);

        Assert.False(File.Exists(archive));

        Assert.Contains(
            result.Issues,
            issue => issue.Code == "backup.recovery_keys_missing");

    }

    /// <summary>
    /// `--restore-master-api-key` adopts the archived key out of the portable recovery document, so
    /// including the master API key in a backup has to put it there. A separate archive entry nothing
    /// reads makes the whole option a dead letter: the restore reports the archive carries no key
    /// while the archive demonstrably does, and the operator is sent to fix a problem they do not
    /// have.
    /// </summary>
    [Fact]
    public async Task An_included_master_api_key_is_carried_by_the_portable_recovery_material()
    {

        BackupStatePaths paths = Paths();

        const string grimoireSecret = "grimoire-secret";

        RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime.Instance.Initialize();

        await CreateMissingAttachmentDatabaseAsync(paths.DatabasePath, grimoireSecret);

        await using (Microsoft.Data.Sqlite.SqliteConnection connection = new(
                         new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                         {

                             DataSource = paths.DatabasePath,

                             Password = DeriveDatabasePassphrase(paths.DatabasePath, grimoireSecret),

                             Pooling = false,

                         }.ToString()))
        {

            await connection.OpenAsync();

            await using Microsoft.Data.Sqlite.SqliteCommand remove = connection.CreateCommand();

            remove.CommandText = "DELETE FROM SessionAttachments;";

            _ = await remove.ExecuteNonQueryAsync();

        }

        string fileEncryptionSecret = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32));

        BackupService service = CreateService(
            paths,
            new StubSecretReader(
                SecretStoreReadResult.Ok(grimoireSecret),
                SecretStoreReadResult.Ok(fileEncryptionSecret),
                SecretStoreReadResult.Ok("the archived master key")));

        string archive = Path.Combine(_root, "master-key.arcbackup");

        BackupCreateResult created = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(
                    BackupScope.Full,
                    SessionId: null,
                    Include: [BackupComponent.MasterApiKey],
                    Exclude: []),
                archive,
                Overwrite: false),
            "master key passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        string extracted = Path.Combine(_root, "extracted");

        string scratch = Path.Combine(_root, "extract-scratch");

        Directory.CreateDirectory(extracted);

        Directory.CreateDirectory(scratch);

        BackupArchiveExtraction extraction = await new BackupArchiveCodec(
            new BackupArchiveCodecOptions
            {

                KdfIterations = 10_000,

                ChunkSize = 64 * 1024,

            }).ExtractAsync(
                archive,
                "master key passphrase".AsMemory(),
                extracted,
                scratch,
                CancellationToken.None);

        Assert.Empty(extraction.Issues);

        AdoptingSecretStore adopted = new();

        BackupSecretRewrapResult rewrap = await new BackupSecretRewrapper(adopted)
            .RewrapAsync(
                Path.Combine(extracted, "recovery", "portable-keys.json"),
                restoreMasterApiKey: true,
                CancellationToken.None);

        Assert.Empty(rewrap.Issues);

        Assert.True(rewrap.MasterApiKeyWritten);

        Assert.Equal("the archived master key", adopted.ApiKey);

    }

    /// <summary>
    /// Verification decrypts the whole payload to disk twice over — a plaintext temporary the size of
    /// the archive, and an extraction tree beside it — and the archive is wherever the operator keeps
    /// their backups: a USB stick, a sync root. Neither belongs there. The archive's own volume is
    /// also the one place owner-only permissions may not hold, which is how a perfectly good archive
    /// on exFAT gets reported as malformed.
    /// </summary>
    [Fact]
    public async Task Verification_scratch_uses_an_owner_only_per_run_os_temp_outside_the_installation()
    {

        string installationRoot = Path.Combine(_root, "installation");

        BackupStatePaths paths = new(
            installationRoot,
            installationRoot,
            Path.Combine(installationRoot, "audit.jsonl"),
            Path.Combine(installationRoot, "guardrails.jsonl"));

        string removable = Path.Combine(_root, "removable");

        Directory.CreateDirectory(removable);

        string archive = Path.Combine(removable, "portable.arcbackup");

        BackupCreateResult created = await CreateService(paths, new CountingSecretReader())
            .CreateAsync(
                new BackupCreateRequest(
                    new BackupPlanRequest(
                        BackupScope.MetadataOnly,
                        SessionId: null,
                        Include: [],
                        Exclude: []),
                    archive,
                    Overwrite: false),
                "scratch passphrase".AsMemory(),
                CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, created.Status);

        byte[] archiveBytes = await File.ReadAllBytesAsync(archive);

        if (Directory.Exists(paths.GrimoireDirectory))
        {

            Directory.Delete(paths.GrimoireDirectory, recursive: true);

        }

        List<string> temporaries = [];

        BackupService service = new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            new BackupArchiveCodec(new BackupArchiveCodecOptions
            {

                KdfIterations = 10_000,

                ChunkSize = 64 * 1024,

                BeforeTemporaryPayloadCleanupForTests = temporaries.Add,

                BeforeTemporaryExtractionCleanupForTests = temporaries.Add,

            }),
            new CountingSecretReader(),
            TimeProvider.System);

        BackupVerifyResult verified = await service.VerifyAsync(
            archive,
            "scratch passphrase".AsMemory(),
            CancellationToken.None);

        Assert.True(
            verified.IsValid,
            string.Join(", ", verified.Issues.Select(static issue => issue.Code)));

        Assert.Equal(2, temporaries.Count);

        string scratchRoot = Assert.Single(
            temporaries
                .Select(static temporary =>
                    Path.GetDirectoryName(temporary)
                    ?? throw new InvalidOperationException(
                        "Verification temporary has no parent directory."))
                .Distinct(StringComparer.Ordinal));

        Assert.StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            + Path.DirectorySeparatorChar,
            scratchRoot,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            Path.GetFullPath(paths.GrimoireDirectory),
            scratchRoot,
            StringComparison.Ordinal);

        Assert.False(Directory.Exists(scratchRoot));

        Assert.False(Directory.Exists(paths.GrimoireDirectory));

        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(archive));

        Assert.Equal(
            [archive],
            Directory.EnumerateFileSystemEntries(removable).Order(StringComparer.Ordinal));

    }

    [Fact]
    public async Task Same_size_source_mutation_after_inventory_is_rejected()
    {

        BackupStatePaths paths = Paths();

        string configurationPath = Path.Combine(paths.GrimoireDirectory, "arcanum.json");

        await File.WriteAllTextAsync(configurationPath, "{\"a\":1}");

        bool mutated = false;

        BackupService service = CreateService(
            paths,
            new CountingSecretReader(),
            _ =>
            {

                if (mutated)
                {

                    return;

                }

                mutated = true;

                File.WriteAllText(configurationPath, "{\"b\":2}");

            });

        string archive = Path.Combine(_root, "mutated.arcbackup");

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(
                    BackupScope.ConfigurationAndAuthoredAssets,
                    SessionId: null,
                    Include: [],
                    Exclude: []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None));

        Assert.False(File.Exists(archive));

        Assert.Equal("{\"b\":2}", await File.ReadAllTextAsync(configurationPath));

    }

    private BackupStatePaths Paths() => new(
        _root,
        _root,
        Path.Combine(_root, "audit.jsonl"),
        Path.Combine(_root, "guardrails.jsonl"));

    private static BackupService CreateService(
        BackupStatePaths paths,
        IBackupSecretSnapshotReader secrets,
        Action<BackupInventory>? afterInventoryBuilt = null) =>
        new(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            new BackupArchiveCodec(new BackupArchiveCodecOptions
            {

                KdfIterations = 10_000,

                ChunkSize = 64 * 1024,

            }),
            secrets,
            TimeProvider.System)
        {

            AfterInventoryBuiltForTests = afterInventoryBuilt,

        };

    private static async Task CreateMissingAttachmentDatabaseAsync(
        string path,
        string grimoireSecret)
    {

        RetroDownfall.Arcanum.Infrastructure.Data.SqliteNativeRuntime.Instance.Initialize();

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(
            GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(path, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            grimoireSecret,
            salt);

        CryptographicOperations.ZeroMemory(salt);

        await using Microsoft.Data.Sqlite.SqliteConnection connection = new(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {

                DataSource = path,

                Password = passphrase,

                Pooling = false,

            }.ToString());

        await connection.OpenAsync();

        await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE SessionAttachments (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NULL,
                RelativePath TEXT NOT NULL,
                EncryptionVersion INTEGER NOT NULL,
                EncryptionKeyId TEXT NULL
            );
            CREATE TABLE UploadedFiles (
                Id TEXT PRIMARY KEY,
                EncryptionVersion INTEGER NOT NULL,
                EncryptionKeyId TEXT NULL
            );
            CREATE TABLE Batches (
                Id TEXT PRIMARY KEY,
                InputFileId TEXT NOT NULL,
                OutputFileId TEXT NULL,
                ErrorFileId TEXT NULL
            );
            INSERT INTO SessionAttachments VALUES (
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                'missing.bin',
                0,
                NULL);
            """;

        _ = await command.ExecuteNonQueryAsync();

    }

    private static string DeriveDatabasePassphrase(
        string databasePath,
        string grimoireSecret)
    {

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(databasePath);

        byte[] salt = sidecar.GetSaltBytes();

        try
        {

            return GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                grimoireSecret,
                salt);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(salt);

        }

    }

    private sealed class CountingSecretReader(
        SecretStoreReadResult? grimoire = null) : IBackupSecretSnapshotReader
    {

        public int ReadCount { get; private set; }

        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync()
        {

            ReadCount++;

            return Task.FromResult(grimoire ?? SecretStoreReadResult.Missing());

        }

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync()
        {

            ReadCount++;

            return Task.FromResult(SecretStoreReadResult.Missing());

        }

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync()
        {

            ReadCount++;

            return Task.FromResult(SecretStoreReadResult.Missing());

        }

    }

    private sealed class StubSecretReader(
        SecretStoreReadResult grimoire,
        SecretStoreReadResult fileEncryptionKeys,
        SecretStoreReadResult masterApiKey) : IBackupSecretSnapshotReader
    {

        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
            Task.FromResult(grimoire);

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
            Task.FromResult(fileEncryptionKeys);

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
            Task.FromResult(masterApiKey);

    }

    /// <summary>The clean machine a portable archive is restored onto: it holds nothing until the re-wrap writes it.</summary>
    private sealed class AdoptingSecretStore : ISecretStore
    {

        public string? ApiKey { get; private set; }

        public string? GrimoireSecret { get; private set; }

        public string? FileEncryptionSecret { get; private set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                ApiKey is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(ApiKey));

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

    private sealed class RecordingOperationJournal :
        ILongRunningOperationCoordinator,
        ILongRunningOperationStore
    {

        public Guid OperationId { get; } = Guid.NewGuid();

        public string? CreatedKind { get; private set; }

        public int CheckpointCount { get; private set; }

        public List<int> CheckpointVersions { get; } = [];

        public List<byte[]> CheckpointPayloads { get; } = [];

        private int CurrentCheckpointVersion { get; set; }

        public bool Completed { get; private set; }

        public bool Failed { get; private set; }

        private LongRunningOperation Operation(
            LongRunningOperationState state = LongRunningOperationState.Running,
            long revision = 1) =>
            new(
                OperationId,
                LongRunningOperationKinds.BackupCreate,
                state,
                LongRunningOperationRecoveryPolicy.AbandonSafely,
                RootOperationId: null,
                ParentOperationId: null,
                SessionId: null,
                RunId: null,
                InferenceRunId: null,
                BudgetReservationId: null,
                IdempotencyClaimId: null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                state == LongRunningOperationState.Completed ? DateTimeOffset.UtcNow : null,
                "test-owner",
                DateTimeOffset.UtcNow.AddMinutes(15),
                AttemptCount: 1,
                CheckpointVersion: CurrentCheckpointVersion,
                CheckpointPayload: null,
                CheckpointReference: null,
                PublicSummary: "test",
                TerminalErrorCode: null,
                revision);

        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {

            CreatedKind = request.Kind;

            return Task.FromResult(new LongRunningOperationLeaseResult(true, Operation()));

        }

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken = default)
        {

            if (expectedCheckpointVersion != CurrentCheckpointVersion)
            {

                return Task.FromResult(false);

            }

            CheckpointCount++;

            CurrentCheckpointVersion = checkpointVersion;

            CheckpointVersions.Add(checkpointVersion);

            if (checkpointPayload is not null)
            {

                CheckpointPayloads.Add([.. checkpointPayload]);

            }

            return Task.FromResult(true);

        }

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {

            Completed = true;

            return Task.FromResult(true);

        }

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken = default)
        {

            Failed = true;

            return Task.FromResult(true);

        }

        public Task<LongRunningOperation?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LongRunningOperation?>(Operation(revision: 1 + CheckpointCount));

        public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LongRunningOperationRequestIdentity?>(null);

        public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
            Guid requestedOperationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LongRunningOperationRequestIdentityMatch?>(null);

        public Task<LongRunningOperation> CreateAsync(
            LongRunningOperationCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LongRunningOperation?> TryStartSingleFlightAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
            LongRunningOperationQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
            DateTimeOffset utcNow,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        Task<bool> ILongRunningOperationStore.HeartbeatAsync(
            Guid operationId,
            string ownerId,
            DateTimeOffset utcNow,
            DateTimeOffset leaseExpiresAt,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> SaveCheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryTransitionAsync(
            Guid operationId,
            long expectedRevision,
            string? ownerId,
            LongRunningOperationState state,
            DateTimeOffset utcNow,
            string? terminalErrorCode = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> RequestCancellationAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ResetForRetryAsync(
            Guid operationId,
            long expectedRevision,
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}
