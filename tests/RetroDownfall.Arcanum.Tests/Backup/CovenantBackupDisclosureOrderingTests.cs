using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// Where the two backup disclosure barriers sit relative to the effects they account for.
/// </summary>
/// <remarks>
/// The decisive cases are the refusals. A barrier that ran after its effect would still produce an
/// archive when the acknowledgement failed, so proving that a refused commit leaves no snapshot and
/// no archive is what proves the ordering — far more directly than instrumenting the write path
/// (§10.13).
/// </remarks>
public sealed class CovenantBackupDisclosureOrderingTests : IDisposable
{

    private const string GrimoireSecret = "grimoire-secret";

    /// <summary>
    /// The native provider has to be installed before the first connection is constructed. Doing it
    /// here keeps a filtered run from depending on some earlier suite having initialized it.
    /// </summary>
    static CovenantBackupDisclosureOrderingTests() => SqliteNativeRuntime.Instance.Initialize();

    private readonly string _root =
        Directory.CreateTempSubdirectory("arcanum-backup-disclosure-").FullName;

    [Fact]
    public async Task A_full_backup_commits_the_snapshot_receipt_then_the_archive_receipt()
    {

        RecordingBoundary boundary = new();

        RecordingGate gate = new();

        BackupService service = await CreateServiceAsync(gate, boundary);

        string archive = Path.Combine(_root, "protected.arcbackup");

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, null, [], []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, result.Status);

        Assert.Equal(
            [
                CovenantBackupDisclosureEffect.SnapshotRead,
                CovenantBackupDisclosureEffect.ArchiveWrite,
            ],
            boundary.Effects);

        // Neither receipt names where the archive went, only a digest of it. Storing the destination
        // itself would recreate inside the journal the exposure the journal exists to account for.
        Assert.All(boundary.Destinations, static digest => Assert.True(digest.IsValid));

    }

    [Fact]
    public async Task A_full_backup_uses_one_installation_read_lease_and_no_nested_scoped_lease()
    {

        RecordingBoundary boundary = new();

        RecordingGate gate = new();

        BackupService service = await CreateServiceAsync(gate, boundary);

        string archive = Path.Combine(_root, "leased.arcbackup");

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, null, [], []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(BackupCreateStatus.Complete, result.Status);

        // Exactly one, and it is the all-scope lease. The recording gate throws on every scoped
        // acquisition, so a nested Global or Campaign lease anywhere under the backup would have
        // failed the run rather than passing quietly.
        Assert.Equal(1, gate.InstallationReadAcquisitions);

        // Released only after the archive exists, never between the snapshot and the last byte.
        Assert.Equal(1, gate.Releases);

        Assert.True(File.Exists(archive));

    }

    [Fact]
    public async Task A_refused_snapshot_acknowledgement_produces_no_snapshot_and_no_archive()
    {

        RecordingBoundary boundary = new() { RefuseSnapshotRead = true };

        RecordingGate gate = new();

        BackupService service = await CreateServiceAsync(gate, boundary);

        string archive = Path.Combine(_root, "refused-snapshot.arcbackup");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.CreateAsync(
                new BackupCreateRequest(
                    new BackupPlanRequest(BackupScope.Full, null, [], []),
                    archive,
                    Overwrite: false),
                "recovery passphrase".AsMemory(),
                CancellationToken.None));

        Assert.False(File.Exists(archive));

        // The archive barrier is never reached either: a backup that could not account for its read
        // has nothing to account for a write of.
        Assert.DoesNotContain(CovenantBackupDisclosureEffect.ArchiveWrite, boundary.Effects);

        // And the lease it acquired was still released.
        Assert.Equal(1, gate.Releases);

    }

    [Fact]
    public async Task A_refused_archive_acknowledgement_produces_no_archive()
    {

        RecordingBoundary boundary = new() { RefuseArchiveWrite = true };

        RecordingGate gate = new();

        BackupService service = await CreateServiceAsync(gate, boundary);

        string archive = Path.Combine(_root, "refused-archive.arcbackup");

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.CreateAsync(
                new BackupCreateRequest(
                    new BackupPlanRequest(BackupScope.Full, null, [], []),
                    archive,
                    Overwrite: false),
                "recovery passphrase".AsMemory(),
                CancellationToken.None));

        Assert.False(File.Exists(archive));

        // The snapshot receipt is retained. A failed attempt that already read protected pages is
        // still a physical read, and dropping its receipt would understate what happened.
        Assert.Contains(CovenantBackupDisclosureEffect.SnapshotRead, boundary.Effects);

        Assert.Equal(1, gate.Releases);

    }

    [Fact]
    public async Task With_the_gate_off_a_backup_acknowledges_and_leases_nothing()
    {

        RecordingBoundary boundary = new();

        RecordingGate gate = new();

        BackupService service = await CreateServiceAsync(covenant: null);

        string archive = Path.Combine(_root, "unprotected.arcbackup");

        BackupCreateResult result = await service.CreateAsync(
            new BackupCreateRequest(
                new BackupPlanRequest(BackupScope.Full, null, [], []),
                archive,
                Overwrite: false),
            "recovery passphrase".AsMemory(),
            CancellationToken.None);

        Assert.Equal(
            BackupCreateStatus.Complete,
            result.Status);

        Assert.Empty(boundary.Effects);
        Assert.Equal(0, gate.InstallationReadAcquisitions);

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a suite over.
        }

    }

    private Task<BackupService> CreateServiceAsync(
        RecordingGate gate,
        RecordingBoundary boundary) =>
        CreateServiceAsync(new CovenantBackupServices(gate, boundary));

    private async Task<BackupService> CreateServiceAsync(CovenantBackupServices? covenant)
    {

        BackupStatePaths paths = new(
            _root,
            _root,
            Path.Combine(_root, "audit.jsonl"),
            Path.Combine(_root, "guardrails.jsonl"));

        Directory.CreateDirectory(paths.AttachmentsDirectory);

        if (!File.Exists(paths.DatabasePath))
        {

            await CreateGrimoireAsync(paths.DatabasePath);

        }

        return new BackupService(
            paths,
            new BackupInventoryPlanner(paths),
            new BackupDatabaseSnapshotter(),
            new BackupArchiveCodec(new BackupArchiveCodecOptions
            {

                KdfIterations = 10_000,

                ChunkSize = 64 * 1024,

            }),
            new FixedSecretReader(),
            TimeProvider.System,
            passphraseSource: null,
            operationCoordinator: null,
            operationStore: null,
            covenant);

    }

    /// <summary>
    /// A minimal encrypted Grimoire with the tables a full inventory expects to find.
    /// </summary>
    private static async Task CreateGrimoireAsync(string path)
    {

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecar.Create(GrimoireKeyDerivation.KdfVersion2);

        GrimoireKdfSidecarFile.Write(path, sidecar);

        byte[] salt = sidecar.GetSaltBytes();

        string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
            GrimoireSecret,
            salt);

        System.Security.Cryptography.CryptographicOperations.ZeroMemory(salt);

        await using Microsoft.Data.Sqlite.SqliteConnection connection = new(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,

                Password = passphrase,

                Pooling = false,
            }.ToString());

        await connection.OpenAsync(CancellationToken.None);

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
            """;

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private sealed class FixedSecretReader : IBackupSecretSnapshotReader
    {

        public Task<SecretStoreReadResult> ReadGrimoireSecretAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(GrimoireSecret));

        public Task<SecretStoreReadResult> ReadFileEncryptionKeysAsync() =>
            Task.FromResult(
                SecretStoreReadResult.Ok(
                    Convert.ToBase64String(
                        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));

        public Task<SecretStoreReadResult> ReadMasterApiKeyAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("scratch-master-key"));

    }

    /// <summary>
    /// Records which effects were acknowledged, and can refuse either one.
    /// </summary>
    private sealed class RecordingBoundary : ICovenantBackupDisclosureBoundary
    {

        internal List<CovenantBackupDisclosureEffect> Effects { get; } = [];

        internal List<CovenantDigest> Destinations { get; } = [];

        internal bool RefuseSnapshotRead { get; init; }

        internal bool RefuseArchiveWrite { get; init; }

        public Task<Result<CovenantBackupDisclosureAcknowledgement>> BeforeSnapshotReadAsync(
            Guid operationId,
            CovenantDigest backupIdentity,
            CovenantDigest destinationIdentity,
            CancellationToken cancellationToken) =>
            AcknowledgeAsync(
                operationId,
                CovenantBackupDisclosureEffect.SnapshotRead,
                destinationIdentity,
                RefuseSnapshotRead);

        public Task<Result<CovenantBackupDisclosureAcknowledgement>> BeforeArchiveWriteAsync(
            Guid operationId,
            CovenantDigest backupIdentity,
            CovenantDigest destinationIdentity,
            CancellationToken cancellationToken) =>
            AcknowledgeAsync(
                operationId,
                CovenantBackupDisclosureEffect.ArchiveWrite,
                destinationIdentity,
                RefuseArchiveWrite);

        private Task<Result<CovenantBackupDisclosureAcknowledgement>> AcknowledgeAsync(
            Guid operationId,
            CovenantBackupDisclosureEffect effect,
            CovenantDigest destinationIdentity,
            bool refuse)
        {

            if (refuse)
            {

                return Task.FromResult(
                    Result<CovenantBackupDisclosureAcknowledgement>.Failure(
                        new Error(ErrorCodes.Covenant.MaintenanceFailed, "The journal refused.")));

            }

            Effects.Add(effect);

            Destinations.Add(destinationIdentity);

            return Task.FromResult(
                Result<CovenantBackupDisclosureAcknowledgement>.Success(
                    new CovenantBackupDisclosureAcknowledgement(
                        operationId,
                        effect,
                        (ulong)Effects.Count,
                        Digest(1),
                        Digest(2))));

        }

        private static CovenantDigest Digest(byte seed) => new([.. Enumerable.Repeat(seed, 32)]);

    }

    /// <summary>
    /// A gate that hands out exactly one all-scope read lease and refuses everything else.
    /// </summary>
    /// <remarks>
    /// Every other acquisition throws on purpose. A backup that reached for a nested Global or
    /// Campaign lease would fail the run loudly instead of passing while quietly holding a capability
    /// the drain protocol does not expect it to have.
    /// </remarks>
    private sealed class RecordingGate : ICovenantOperationGate
    {

        internal int InstallationReadAcquisitions { get; private set; }

        internal int Releases { get; private set; }

        public ValueTask<Result<CovenantInstallationReadLease>> AcquireInstallationReadAsync(
            CancellationToken cancellationToken)
        {

            InstallationReadAcquisitions++;

            return ValueTask.FromResult(
                Result<CovenantInstallationReadLease>.Success(
                    new CovenantInstallationReadLease(new Registration(this))));

        }

        public ValueTask<Result<CovenantReadLease>> AcquireReadAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire a nested scoped read lease.");

        public ValueTask<Result<CovenantWriteLease>> AcquireWriteAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire a write lease.");

        public ValueTask<Result<CovenantTurnLease>> AcquireTurnAsync(
            CanonicalCampaignContext campaign,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire a turn lease.");

        public ValueTask<Result<CovenantMcpLease>> AcquireMcpAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire an MCP lease.");

        public ValueTask<Result<CovenantAcceleratorLease>> AcquireAcceleratorAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire an accelerator lease.");

        public ValueTask<Result<CovenantCleanupLease>> AcquireCleanupAsync(
            CovenantOperationScope scope,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire a cleanup lease.");

        public ValueTask<Result<CovenantCampaignExclusiveLease>> AcquireCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not close a Campaign scope.");

        public ValueTask<Result<CovenantProtectedTransferLease>> AcquireProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not acquire a transfer lease.");

        public ValueTask<Result<CovenantExclusiveLease>> AcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not close admission.");

        public ValueTask<Result<CovenantExclusiveLease>> ResumeOrAcquireExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup must not resume or close admission.");

        public ValueTask<Result<CovenantCampaignExclusiveLease>> ResumeCampaignExclusiveAsync(
            Guid campaignId,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup resumes nothing.");

        public ValueTask<Result<CovenantProtectedTransferLease>> ResumeProtectedTransferAsync(
            ProtectedTransferScope scope,
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup resumes nothing.");

        public ValueTask<Result<CovenantExclusiveLease>> ResumeExclusiveAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A backup resumes nothing.");

        private sealed class Registration(RecordingGate gate) : ICovenantLeaseRegistration
        {

            public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
                Guid.NewGuid(),
                1,
                CovenantLeaseKind.InstallationRead,
                CovenantLeaseCoverage.Installation,
                null,
                Guid.NewGuid(),
                1,
                1,
                1,
                null,
                null,
                null,
                null,
                null,
                false);

            public CancellationToken Revocation => CancellationToken.None;

            public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
                ValueTask.FromResult(Result.Success());

            public ValueTask ReleaseAsync()
            {

                gate.Releases++;

                return ValueTask.CompletedTask;

            }

        }

    }

}
