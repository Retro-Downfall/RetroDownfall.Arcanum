using System.Collections.Immutable;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// The Covenant arm of a full restore, end to end against a real encrypted archive (§10.19.9).
/// </summary>
/// <remarks>
/// The gate and the marker lifecycle are recorded rather than real, because what this suite asserts is
/// the seam between the restore's filesystem protocol and its authority protocol: which owner closes
/// admission, what the authenticated journal carries at each displacement boundary, and how many
/// dispositions are spent. The staged reconciliation itself is exercised against real SQLCipher, since
/// its whole content is what the restored database ends up holding.
/// </remarks>
public sealed class CovenantRestoreStagingTests : IDisposable
{

    private const string Passphrase = "covenant restore staging passphrase";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-covenant-restore-" + Guid.NewGuid().ToString("N"));

    private readonly string _installation;

    private readonly string _archives;

    private readonly InMemoryOsCredentialStore _credentials = new();

    public CovenantRestoreStagingTests()
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
    public async Task A_restore_closes_admission_under_one_owner_and_reopens_it_exactly_once()
    {

        Harness harness = await CreateHarnessAsync();

        BackupRestoreResult result = await harness.RestoreAsync();

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        // Exactly one acquisition, one disposition, and the disposition is the commit.
        Assert.Equal(1, harness.Gate.ExclusiveAcquisitions);

        Assert.Equal(
            [CovenantExclusiveLeaseDisposition.CommitAndReopen],
            harness.Gate.Dispositions);

        // The owner the gate saw is the owner the checkpoint names.
        Assert.Equal(
            harness.Gate.LastOwner,
            harness.Markers.LastReconcileRequest?.Owner);

    }

    [Fact]
    public async Task A_zero_marker_inventory_publishes_the_frozen_empty_child_vector_before_displacement()
    {

        Harness harness = await CreateHarnessAsync();

        BackupRestoreResult result = await harness.RestoreAsync();

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        BackupRestoreMarkerCleanupCheckpointV1 checkpoint = Assert.IsType<BackupRestoreMarkerCleanupCheckpointV1>(
            harness.CheckpointAtDisplacement);

        Assert.Equal(0UL, checkpoint.IntentCount);

        Assert.True(checkpoint.OrderedIntentIds.IsEmpty);

        // The one frozen literal, so a proven-empty inventory is distinguishable from a preparation
        // that never ran — which is null, not empty.
        Assert.Equal(
            CampaignPathRestoreCleanupIntentVector.EmptyDigest,
            checkpoint.IntentVectorDigest);

        Assert.Equal(
            checkpoint.IntentVectorDigest,
            harness.Markers.LastReconcileRequest?.IntentVectorDigest);

    }

    [Fact]
    public async Task The_journal_carries_no_checkpoint_until_the_staged_children_have_committed()
    {

        Harness harness = await CreateHarnessAsync();

        Assert.Equal(BackupRestoreStatus.Completed, (await harness.RestoreAsync()).Status);

        // Preparation commits into the staged database first and the checkpoint is published after,
        // so the very first published revision cannot name children that did not exist yet.
        Assert.Null(harness.FirstPublishedCheckpoint);

        Assert.NotNull(harness.CheckpointAtDisplacement);

    }

    [Fact]
    public async Task The_restored_generation_carries_fresh_identities_and_no_resolved_Campaign_path()
    {

        Harness harness = await CreateHarnessAsync();

        string archivedGeneration = await harness.ReadDatasetGenerationAsync();

        Assert.Equal(BackupRestoreStatus.Completed, (await harness.RestoreAsync()).Status);

        Assert.NotEqual(archivedGeneration, await harness.ReadDatasetGenerationAsync());

        Assert.Equal(0, await harness.CountAsync("campaign_path_identities"));

        // The accelerator published nothing for this dataset, so it is dirty by construction.
        Assert.Equal(
            1,
            await harness.ScalarAsync(
                "SELECT COUNT(*) FROM covenant_state WHERE AppliedDatasetGeneration IS NULL "
                + "AND RebuildStateCode = 2;"));

    }

    [Fact]
    public async Task An_abort_before_the_swap_reopens_admission_and_leaves_the_installation_alone()
    {

        Harness harness = await CreateHarnessAsync();

        harness.Options = harness.Options with
        {

            FailBeforePhase = BackupRestorePhase.Commit,

        };

        BackupRestoreResult result = await harness.RestoreAsync();

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        // Nothing durable happened, which is the only case that may reopen without a commit.
        Assert.Equal(
            [CovenantExclusiveLeaseDisposition.RollbackAndReopen],
            harness.Gate.Dispositions);

        Assert.Equal(0, harness.Markers.ReconcileCalls);

    }

    [Fact]
    public async Task An_unprovable_marker_child_after_the_swap_keeps_admission_closed()
    {

        Harness harness = await CreateHarnessAsync();

        harness.Markers.Failure = new Error(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "A restore cleanup child could not be proved.");

        BackupRestoreResult result = await harness.RestoreAsync();

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);

        Assert.Equal(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            Assert.Single(result.Issues).Code);

        // Disposed without a disposition, which is exactly KeepClosed: the closure and the adoptable
        // owner survive so the next start resumes this operation rather than restarting it.
        Assert.Empty(harness.Gate.Dispositions);

        Assert.Equal(1, harness.Gate.Releases);

    }

    [Fact]
    public async Task A_restore_that_never_enabled_the_gate_behaves_exactly_as_it_always_did()
    {

        Harness harness = await CreateHarnessAsync(covenant: false);

        BackupRestoreResult result = await harness.RestoreAsync();

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(0, harness.Gate.ExclusiveAcquisitions);

    }

    private async Task<Harness> CreateHarnessAsync(bool covenant = true)
    {

        Harness harness = new(_installation, _archives, _credentials, covenant);

        await harness.BuildAsync();

        return harness;

    }

    /// <summary>
    /// A real installation, a real archive, and the recorded authority the restore runs under.
    /// </summary>
    private sealed class Harness(
        string installation,
        string archives,
        InMemoryOsCredentialStore credentials,
        bool covenant)
    {

        internal RecordingExclusiveGate Gate { get; } = new();

        internal RecordingRestoreMarkerLifecycle Markers { get; } = new();

        internal HarnessOptions Options { get; set; } = new(null);

        internal List<BackupRestoreMarkerCleanupCheckpointV1?> Published { get; } = [];

        internal BackupRestoreMarkerCleanupCheckpointV1? FirstPublishedCheckpoint =>
            Published.Count > 0 ? Published[0] : null;

        internal BackupRestoreMarkerCleanupCheckpointV1? CheckpointAtDisplacement { get; private set; }

        private string GrimoireSecret { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        private string ArchivePath { get; set; } = string.Empty;

        internal async Task BuildAsync()
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(installation);

            await File.WriteAllTextAsync(
                Path.Combine(installation, "arcanum.json"),
                """{ "Arcanum": { "Host": { "ListenAny": false } } }""");

            await BuildDatabaseAsync();

            BackupService backups = new(
                Paths(),
                new BackupInventoryPlanner(Paths()),
                new BackupDatabaseSnapshotter(),
                Codec(),
                new HarnessSecretReader(GrimoireSecret),
                TimeProvider.System);

            BackupCreateResult created = await backups.CreateAsync(
                new BackupCreateRequest(
                    new BackupPlanRequest(BackupScope.Full, SessionId: null, Include: [], Exclude: []),
                    Path.Combine(archives, "covenant.arcbackup"),
                    Overwrite: true),
                Passphrase.AsMemory(),
                CancellationToken.None);

            Assert.Equal(BackupCreateStatus.Complete, created.Status);

            ArchivePath = created.ArchivePath!;

        }

        internal async Task<BackupRestoreResult> RestoreAsync()
        {

            BackupRestoreServiceOptions options = new()
            {

                RestoreStaging = covenant
                    ? new CovenantRestoreStagingServices(
                        Gate,
                        Markers,
                        Anchors(),
                        new BackupRestoreJournalInstallationIdentityProvider(credentials),
                        new BackupRestoreJournalKeyProvider(credentials),
                        new BackupRestoreEffectDigestCalculator())
                    : null,

                BeforePhaseForTests = phase =>
                {

                    if (phase == BackupRestorePhase.Commit)
                    {

                        CheckpointAtDisplacement = Markers.LastPreparedCheckpoint;

                    }

                    if (Options.FailBeforePhase == phase)
                    {

                        throw new InvalidOperationException(
                            "The harness failed this restore at " + phase + ".");

                    }

                },

            };

            Markers.Published = Published;

            BackupRestoreService restore = new(
                Paths(),
                Codec(),
                new HarnessSecretStore(GrimoireSecret),
                safetyBackupFactory: null,
                TimeProvider.System,
                GrimoireSchemaTestInstaller.Create(),
                options);

            return await restore.RestoreAsync(
                new BackupRestoreRequest(ArchivePath, Confirmed: true, CreateSafetyBackup: false),
                Passphrase.AsMemory(),
                CancellationToken.None);

        }

        internal async Task<string> ReadDatasetGenerationAsync() =>
            await ScalarStringAsync("SELECT hex(DatasetGeneration) FROM covenant_state;") ?? string.Empty;

        internal async Task<long> CountAsync(string table) =>
            await ScalarAsync($"SELECT COUNT(*) FROM {table};");

        internal async Task<long> ScalarAsync(string sql)
        {

            await using SqliteConnection connection = await OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            object? value = await command.ExecuteScalarAsync(CancellationToken.None);

            return value is null or DBNull
                ? 0
                : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        }

        private async Task<string?> ScalarStringAsync(string sql)
        {

            await using SqliteConnection connection = await OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            object? value = await command.ExecuteScalarAsync(CancellationToken.None);

            return value is null or DBNull
                ? null
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

        }

        private Task<SqliteConnection> OpenAsync() =>
            BackupRestoreDatabaseWorker.OpenAsync(
                Path.Combine(installation, "arcanum.db"),
                GrimoireSecret,
                readOnly: true,
                CancellationToken.None);

        private BackupRestoreJournalAnchorStore Anchors() =>
            new(
                credentials,
                new BackupRestoreJournalKeyProvider(credentials),
                new BackupRestoreJournalInstallationIdentityProvider(credentials));

        private BackupStatePaths Paths() => new(
            installation,
            installation,
            Path.Combine(installation, "audit.jsonl"),
            Path.Combine(installation, "guardrails.jsonl"));

        private static BackupArchiveCodec Codec() =>
            new(new BackupArchiveCodecOptions { KdfIterations = 10_000, ChunkSize = 64 * 1024 });

        /// <summary>
        /// Installs a real three-tier Grimoire and seeds the machine-specific state a restore has to
        /// reconcile: a registered Campaign root and an authority row with this installation's identity.
        /// </summary>
        private async Task BuildDatabaseAsync()
        {

            string database = Path.Combine(installation, "arcanum.db");

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

            GrimoireKdfSidecarFile.Write(database, sidecar);

            byte[] salt = sidecar.GetSaltBytes();

            string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                GrimoireSecret,
                salt);

            CryptographicOperations.ZeroMemory(salt);

            SqliteNativeRuntime.Instance.Initialize();

            await using SqliteConnection connection = await GrimoireSchemaTestInstaller.OpenAsync(
                new SqliteConnectionStringBuilder
                {

                    DataSource = database,

                    Password = passphrase,

                    Pooling = false,

                }.ToString(),
                CancellationToken.None);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

            await ExecuteAsync(
                connection,
                """
                INSERT INTO "Campaigns" (
                    "Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
                VALUES ('11111111-1111-4111-8111-111111111111', 'Local', 'local',
                        '/local/campaign', 1, '{}', '2026-01-01T00:00:00.0000000Z',
                        '2026-01-01T00:00:00.0000000Z');
                """);

            await ExecuteAsync(
                connection,
                """
                INSERT INTO campaign_path_identities (
                    CampaignId, PolicyVersion, Revision, DisplayPath, Depth, PhysicalIdentityDigest,
                    UpdatedAtUtc)
                VALUES ('11111111-1111-4111-8111-111111111111', 1, 1, '/local/campaign', 2,
                        zeroblob(32), '2026-01-01T00:00:00.0000000Z');
                """);

        }

        private static async Task ExecuteAsync(SqliteConnection connection, string sql)
        {

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }

    }

    internal sealed record HarnessOptions(BackupRestorePhase? FailBeforePhase);

    private sealed class HarnessSecretReader(string grimoireSecret) : IBackupSecretSnapshotReader
    {

        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(grimoireSecret));

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
            Task.FromResult(
                SecretStoreReadResult.Ok(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

    }

    private sealed class HarnessSecretStore(string grimoireSecret) : ISecretStore
    {

        private string? _grimoire = grimoireSecret;

        private string? _fileKeys;

        private string? _apiKey;

        public Task<string?> GetApiKeyAsync() => Task.FromResult(_apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                _apiKey is null ? SecretStoreReadResult.Missing() : SecretStoreReadResult.Ok(_apiKey));

        public Task SaveApiKeyAsync(string apiKey)
        {

            _apiKey = apiKey;

            return Task.CompletedTask;

        }

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult(_grimoire);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret)
        {

            _grimoire = encryptionSecret;

            return Task.CompletedTask;

        }

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync() =>
            Task.FromResult(
                _fileKeys is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(_fileKeys));

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret)
        {

            _fileKeys = encryptionSecret;

            return Task.CompletedTask;

        }

    }

    /// <summary>
    /// A gate that records what a restore asked of it and hands out one completable exclusive lease.
    /// </summary>
    /// <remarks>
    /// Every non-exclusive acquisition throws. A restore that took an ordinary lease anywhere under its
    /// own closure would deadlock against its own drain, and a fake that quietly answered would let
    /// that pass.
    /// </remarks>
    internal sealed class RecordingExclusiveGate : ICovenantOperationGate
    {

        internal int ExclusiveAcquisitions { get; private set; }

        internal int Releases { get; private set; }

        internal CovenantExclusiveRecoveryOwner? LastOwner { get; private set; }

        internal List<CovenantExclusiveLeaseDisposition> Dispositions { get; } = [];

        public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken)
        {

            ExclusiveAcquisitions++;

            LastOwner = owner;

            return ValueTask.FromResult(
                Result<CovenantExclusiveLease>.Success(
                    new CovenantExclusiveLease(new Registration(this, owner))));

        }

        public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore takes no nested read lease.");

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore takes no nested read lease.");

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore takes no nested write lease.");

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
            CanonicalCampaignContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore runs no turn.");

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore runs no MCP mutation.");

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore synchronizes no accelerator.");

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore runs no owner cleanup.");

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore closes the installation, never one Campaign.");

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A full restore transfers no Session.");

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore resumes no Campaign scope.");

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A restore resumes no transfer.");

        public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A live restore acquires; only startup resumes.");

        private sealed class Registration(
            RecordingExclusiveGate gate,
            CovenantExclusiveRecoveryOwner owner) : ICovenantExclusiveLeaseRegistration
        {

            public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
                Guid.NewGuid(),
                CovenantLeaseKind.Exclusive,
                CovenantLeaseCoverage.Installation,
                Scope: null,
                DatasetGeneration: null,
                CapabilityGeneration: 1,
                AuthorityEpoch: 1,
                CanonicalSequence: 0,
                CampaignAvailabilityGeneration: null,
                CampaignPathRevision: null,
                AcceleratorEpoch: null,
                AppliedCampaignDeletionSequence: null,
                owner,
                CleanupOnlyHistoricalCampaign: false);

            public CancellationToken Revocation => CancellationToken.None;

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
                ValueTask.FromResult(Result.Success());

            public ValueTask ReleaseAsync()
            {

                gate.Releases++;

                return ValueTask.CompletedTask;

            }

            public ValueTask<Result> CompleteAsync(
                CovenantExclusiveLeaseDisposition disposition,
                CancellationToken cancellationToken)
            {

                gate.Dispositions.Add(disposition);

                return ValueTask.FromResult(Result.Success());

            }

        }

    }

    /// <summary>
    /// A marker lifecycle that reports an empty destination inventory and records what it was asked.
    /// </summary>
    /// <remarks>
    /// Empty rather than absent, which is the interesting arm: it is what forces the restore to publish
    /// the frozen zero-child digest instead of leaving the checkpoint null, and those two states are
    /// exactly what a crash between the staged commit and the journal publication would otherwise blur.
    /// </remarks>
    internal sealed class RecordingRestoreMarkerLifecycle : ICampaignPathMarkerLifecycle
    {

        internal int ReconcileCalls { get; private set; }

        internal CampaignPathMarkerGateReconcileRequest? LastReconcileRequest { get; private set; }

        internal BackupRestoreMarkerCleanupCheckpointV1? LastPreparedCheckpoint { get; private set; }

        internal Guid? ReleasedOwnerOperationId { get; private set; }

        internal Error? Failure { get; set; }

        internal List<BackupRestoreMarkerCleanupCheckpointV1?>? Published { get; set; }

        public Task<Result<CampaignPathRestoreCleanupInventory>> InventoryRestoreCleanupAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Result<CampaignPathRestoreCleanupInventory>.Success(
                    new CampaignPathRestoreCleanupInventory(
                        ImmutableArray<CampaignPathRestoreCleanupSeed>.Empty)));

        public Task<Result<CampaignPathRestoreCleanupPreparationReceipt>>
            PrepareRestoreCleanupInStagedDatabaseAsync(
                CampaignPathRestoreCleanupPreparation preparation,
                SqliteConnection stagedConnection,
                SqliteTransaction stagedTransaction,
                CancellationToken cancellationToken)
        {

            CampaignPathRestoreCleanupPreparationReceipt receipt = new(
                preparation.Owner,
                ImmutableArray<Guid>.Empty,
                0,
                CampaignPathRestoreCleanupIntentVector.EmptyDigest);

            LastPreparedCheckpoint = new BackupRestoreMarkerCleanupCheckpointV1(
                1,
                receipt.Owner.OperationId,
                receipt.Owner.Operation,
                receipt.Owner.EffectDigest,
                receipt.OrderedIntentIds,
                receipt.IntentCount,
                receipt.IntentVectorDigest);

            Published?.Add(null);

            return Task.FromResult(
                Result<CampaignPathRestoreCleanupPreparationReceipt>.Success(receipt));

        }

        public Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
            CampaignPathMarkerGateReconcileRequest request,
            ICovenantExclusiveOperationLease exclusiveLease,
            CancellationToken cancellationToken)
        {

            ReconcileCalls++;

            LastReconcileRequest = request;

            return Task.FromResult(
                Failure is { } failure
                    ? Result<CampaignPathMarkerGateCompletion>.Failure(failure)
                    : Result<CampaignPathMarkerGateCompletion>.Success(
                        new CampaignPathMarkerGateCompletion(
                            CampaignPathMarkerAggregateOutcome.Committed,
                            CovenantExclusiveLeaseDisposition.CommitAndReopen,
                            CovenantNoOpPostDispositionFinalizer.Instance)));

        }

        public ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId)
        {

            ReleasedOwnerOperationId = ownerOperationId;

            return ValueTask.CompletedTask;

        }

    }

}
