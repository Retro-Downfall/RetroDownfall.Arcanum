using System.Reflection;

using System.Runtime.InteropServices;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

public sealed class BackupService : IBackupService
{

    // Instance member, matching BackupRestoreServiceOptions.BeforePhaseForTests. As a settable
    // static this single-shot hook could be consumed by a different test's CreateAsync, which both
    // stole the callback and mutated the owning test's tree before its own inventory was built.
    internal Action<BackupInventory>? AfterInventoryBuiltForTests { get; init; }

    private static readonly BackupComponent[] DatabaseBackedComponents =
    [
        BackupComponent.GrimoireDatabase,
        BackupComponent.SessionAttachments,
        BackupComponent.UploadedFiles,
        BackupComponent.BatchArtifacts,
    ];

    private readonly BackupStatePaths _paths;

    private readonly BackupInventoryPlanner _planner;

    private readonly BackupDatabaseSnapshotter _snapshotter;

    private readonly BackupArchiveCodec _codec;

    private readonly IBackupSecretSnapshotReader _secrets;

    private readonly TimeProvider _timeProvider;

    private readonly IGrimoireDbPassphraseSource? _passphraseSource;

    private readonly ILongRunningOperationCoordinator? _operationCoordinator;

    private readonly ILongRunningOperationStore? _operationStore;

    /// <summary>
    /// The disclosure and lease capabilities a Covenant-enabled installation backs up under.
    /// </summary>
    /// <remarks>
    /// Absent means the feature was never enabled, and the backup behaves exactly as it always has.
    /// Present means no physical attempt crosses either effect boundary before its receipt commits.
    /// </remarks>
    private readonly CovenantBackupServices? _covenant;

    internal BackupService(
        BackupStatePaths paths,
        BackupInventoryPlanner planner,
        BackupDatabaseSnapshotter snapshotter,
        BackupArchiveCodec codec,
        IBackupSecretSnapshotReader secrets,
        TimeProvider timeProvider)
        : this(
            paths,
            planner,
            snapshotter,
            codec,
            secrets,
            timeProvider,
            passphraseSource: null,
            operationCoordinator: null,
            operationStore: null)
    {

    }

    internal BackupService(
        BackupStatePaths paths,
        BackupInventoryPlanner planner,
        BackupDatabaseSnapshotter snapshotter,
        BackupArchiveCodec codec,
        IBackupSecretSnapshotReader secrets,
        TimeProvider timeProvider,
        IGrimoireDbPassphraseSource? passphraseSource,
        ILongRunningOperationCoordinator? operationCoordinator,
        ILongRunningOperationStore? operationStore,
        CovenantBackupServices? covenant = null)
    {

        _paths = paths;

        _planner = planner;

        _snapshotter = snapshotter;

        _codec = codec;

        _secrets = secrets;

        _timeProvider = timeProvider;

        _passphraseSource = passphraseSource;

        _operationCoordinator = operationCoordinator;

        _operationStore = operationStore;

        _covenant = covenant;

    }

    public async Task<BackupPlan> PlanAsync(
        BackupPlanRequest request,
        CancellationToken cancellationToken = default)
    {

        HashSet<BackupComponent> selected = ResolveSelectedComponents(request);

        string databasePassphrase = string.Empty;

        if (selected.Overlaps(DatabaseBackedComponents[1..]))
        {

            using DatabaseUnlockMaterial unlock = await ReadDatabaseUnlockAsync().ConfigureAwait(false);

            databasePassphrase = unlock.DatabasePassphrase;

        }

        BackupInventory inventory = await _planner.BuildAsync(
            request,
            _paths.DatabasePath,
            databasePassphrase,
            cancellationToken).ConfigureAwait(false);

        return inventory.Plan;

    }

    public async Task<BackupCreateResult> CreateAsync(
        BackupCreateRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        Guid operationId = Guid.NewGuid();

        string outputPath = ResolveOutputPath(request.OutputPath);

        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("The backup output path has no parent directory.");

        Directory.CreateDirectory(outputDirectory);

        string stagingRoot = Path.Combine(
            outputDirectory,
            ".arcanum-backup-stage-" + Guid.NewGuid().ToString("N"));

        OwnedTemporaryDirectory? stagingDirectory = null;

        DatabaseUnlockMaterial? unlock = null;

        DurableBackupOperation? durableOperation = null;

        List<byte[]> sensitiveMemorySources = [];

        // One installation read lease for the whole physical backup, acquired before the inventory is
        // frozen and released only after the archive writer has finished or failed. Every full-scope
        // read runs under this exact lease; a nested Global or Campaign acquisition would let reset
        // drain one scope while another was still being copied into the archive.
        CovenantInstallationReadLease? installationRead = null;

        CovenantBackupDisclosureAcknowledgement? snapshotReceipt = null;

        CovenantBackupDisclosureAcknowledgement? archiveReceipt = null;

        try
        {

            HashSet<BackupComponent> selected = ResolveSelectedComponents(request.Plan);

            bool needsDatabaseSnapshot = selected.Overlaps(DatabaseBackedComponents);

            string inventoryDatabasePath = _paths.DatabasePath;

            string databasePassphrase = string.Empty;

            if (needsDatabaseSnapshot)
            {

                unlock = await ReadDatabaseUnlockAsync().ConfigureAwait(false);

                databasePassphrase = unlock.DatabasePassphrase;

                if (_covenant is { } covenant)
                {

                    Result<CovenantInstallationReadLease> acquired = await covenant.Gate
                        .AcquireInstallationReadAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (acquired.IsFailure)
                    {

                        throw new InvalidOperationException(
                            "A protected backup could not acquire its installation read lease: "
                            + acquired.Error.Message);

                    }

                    installationRead = acquired.Value;

                }

                durableOperation = await StartDurableOperationAsync(
                    request,
                    outputPath,
                    cancellationToken).ConfigureAwait(false);

                if (durableOperation is not null)
                {

                    operationId = durableOperation.OperationId;

                    await SaveCheckpointAsync(
                        durableOperation,
                        request,
                        outputPath,
                        stagingRoot,
                        stagingDirectory,
                        "staging-planned",
                        "Backup staging is planned on the destination filesystem.",
                        cancellationToken).ConfigureAwait(false);

                }

            }

            stagingDirectory = OwnedTemporaryDirectory.Create(stagingRoot);

            if (durableOperation is not null)
            {

                await SaveCheckpointAsync(
                    durableOperation,
                    request,
                    outputPath,
                    stagingRoot,
                    stagingDirectory,
                    "staging-created",
                    "Backup staging was created with a verified filesystem identity.",
                    cancellationToken).ConfigureAwait(false);

            }

            if (needsDatabaseSnapshot)
            {

                inventoryDatabasePath = Path.Combine(
                    stagingRoot,
                    BackupArchivePaths.GrimoireDatabase.Replace(
                        '/',
                        Path.DirectorySeparatorChar));

                // Immediately before page one. A receipt committed after the read would be unable to
                // record the one case it exists for: a snapshot that copied protected pages and then
                // crashed.
                snapshotReceipt = await AcknowledgeSnapshotReadAsync(
                    operationId,
                    outputPath,
                    cancellationToken).ConfigureAwait(false);

                if (durableOperation is null)
                {

                    await _snapshotter.CreateAsync(
                        _paths.DatabasePath,
                        inventoryDatabasePath,
                        databasePassphrase,
                        cancellationToken).ConfigureAwait(false);

                }
                else
                {

                    _ = await RunWithDurableLeaseAsync(
                        durableOperation,
                        async token =>
                        {

                            await _snapshotter.CreateAsync(
                                _paths.DatabasePath,
                                inventoryDatabasePath,
                                databasePassphrase,
                                token).ConfigureAwait(false);

                            return true;

                        },
                        cancellationToken).ConfigureAwait(false);

                }

                await CopyProtectedFileAsync(
                    _paths.KdfPath,
                    inventoryDatabasePath + ".kdf",
                    cancellationToken).ConfigureAwait(false);

                if (durableOperation is not null)
                {

                    await RemoveOperationFromSnapshotAsync(
                        inventoryDatabasePath,
                        databasePassphrase,
                        durableOperation.OperationId,
                        cancellationToken).ConfigureAwait(false);

                }

            }

            BackupInventory inventory = durableOperation is null
                ? await _planner.BuildAsync(
                    request.Plan,
                    inventoryDatabasePath,
                    databasePassphrase,
                    cancellationToken).ConfigureAwait(false)
                : await RunWithDurableLeaseAsync(
                    durableOperation,
                    token => _planner.BuildAsync(
                        request.Plan,
                        inventoryDatabasePath,
                        databasePassphrase,
                        token),
                    cancellationToken).ConfigureAwait(false);

            AfterInventoryBuiltForTests?.Invoke(inventory);

            if (inventory.Plan.Components.Any(
                    static component => component.Status == BackupComponentStatus.Failed))
            {

                await FailDurableOperationBestEffortAsync(
                    durableOperation,
                    "backup.inventory_incomplete").ConfigureAwait(false);

                durableOperation = null;

                return Incomplete(
                    operationId,
                    inventory.Plan,
                    "backup.inventory_incomplete",
                    "Required backup bytes are missing or unsafe to capture.");

            }

            List<PreparedBackupSource> sources = inventory.Files
                .Select(static file => PreparedBackupSource.FromFile(file))
                .ToList();

            BackupPlan effectivePlan = inventory.Plan;

            // Read before the portable recovery document is built, because that document is what
            // carries it: `--restore-master-api-key` adopts the key from the material the re-wrap
            // reads, and the standalone archive entry below is inventory, not a carrier. Splitting
            // the two is what made the restore option a dead letter — it reported an archive that
            // demonstrably held the key as holding none.
            GeneratedSensitiveSource? master = selected.Contains(BackupComponent.MasterApiKey)
                ? await BuildMasterApiKeyAsync().ConfigureAwait(false)
                : null;

            if (master?.Issue is not null)
            {

                await FailDurableOperationBestEffortAsync(
                    durableOperation,
                    master.Issue.Code).ConfigureAwait(false);

                durableOperation = null;

                effectivePlan = MarkComponentFailed(
                    effectivePlan,
                    BackupComponent.MasterApiKey,
                    master.Issue.Message);

                return new BackupCreateResult(
                    BackupCreateStatus.Incomplete,
                    ArchivePath: null,
                    ArchiveBytes: 0,
                    operationId,
                    Manifest: null,
                    effectivePlan,
                    [master.Issue]);

            }

            if (master is not null)
            {

                // Registered for zeroing the moment the bytes exist, not where they are added to the
                // archive: the recovery block below has early returns of its own, and a plaintext
                // credential must not outlive one of them.
                sensitiveMemorySources.Add(master.Bytes!);

            }

            if (selected.Contains(BackupComponent.PortableRecoveryKeys))
            {

                unlock ??= await ReadDatabaseUnlockAsync().ConfigureAwait(false);

                GeneratedSensitiveSource recovery = await BuildRecoveryMaterialAsync(
                    unlock.GrimoireSecret,
                    inventory.RequiredFileEncryptionKeyIds,
                    includeActiveFileKey: request.Plan.Scope == BackupScope.Full,
                    master?.Bytes).ConfigureAwait(false);

                if (recovery.Issue is not null)
                {

                    await FailDurableOperationBestEffortAsync(
                        durableOperation,
                        recovery.Issue.Code).ConfigureAwait(false);

                    durableOperation = null;

                    effectivePlan = MarkComponentFailed(
                        effectivePlan,
                        BackupComponent.PortableRecoveryKeys,
                        recovery.Issue.Message);

                    return new BackupCreateResult(
                        BackupCreateStatus.Incomplete,
                        ArchivePath: null,
                        ArchiveBytes: 0,
                        operationId,
                        Manifest: null,
                        effectivePlan,
                        [recovery.Issue]);

                }

                sensitiveMemorySources.Add(recovery.Bytes!);

                sources.Add(PreparedBackupSource.FromMemory(
                    BackupComponent.PortableRecoveryKeys,
                    BackupArchivePaths.PortableRecoveryKeys,
                    recovery.Bytes!));

            }

            if (master is not null)
            {

                sources.Add(PreparedBackupSource.FromMemory(
                    BackupComponent.MasterApiKey,
                    BackupArchivePaths.MasterApiKey,
                    master.Bytes!));

            }

            if (durableOperation is not null)
            {

                await SaveCheckpointAsync(
                    durableOperation,
                    request,
                    outputPath,
                    stagingRoot,
                    stagingDirectory,
                    "inventory-complete",
                    $"Backup inventory contains {sources.Count} files.",
                    cancellationToken).ConfigureAwait(false);

            }

            sources.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.ArchivePath, right.ArchivePath));

            BackupManifestEntry[] entries = durableOperation is null
                ? await BuildEntriesAsync(
                    sources,
                    cancellationToken).ConfigureAwait(false)
                : await RunWithDurableLeaseAsync(
                    durableOperation,
                    token => BuildEntriesAsync(sources, token),
                    cancellationToken).ConfigureAwait(false);

            effectivePlan = RecalculatePlan(effectivePlan, entries);

            if (durableOperation is not null)
            {

                await SaveCheckpointAsync(
                    durableOperation,
                    request,
                    outputPath,
                    stagingRoot,
                    stagingDirectory,
                    "checksums-complete",
                    $"Backup checksums cover {entries.LongLength} files and {entries.Sum(static entry => entry.Size)} bytes.",
                    cancellationToken).ConfigureAwait(false);

            }

            string schemaVersion = needsDatabaseSnapshot
                ? await ReadSchemaVersionAsync(
                    inventoryDatabasePath,
                    databasePassphrase,
                    cancellationToken).ConfigureAwait(false)
                : "not-applicable";

            BackupManifest manifest = BuildManifest(
                request.Plan,
                effectivePlan,
                schemaVersion,
                entries);

            BackupArchiveSource[] archiveSources = sources
                .Select(static source => source.ToArchiveSource())
                .ToArray();

            // Immediately before the first output byte. The archive is a nonrevocable disclosure, so
            // the accounting has to exist before the file can.
            archiveReceipt = await AcknowledgeArchiveWriteAsync(
                operationId,
                outputPath,
                cancellationToken).ConfigureAwait(false);

            BackupManifest effectiveManifest = durableOperation is null
                ? await _codec.WriteAsync(
                    outputPath,
                    manifest,
                    archiveSources,
                    recoveryPassphrase,
                    request.Overwrite,
                    stagingDirectory.Path,
                    cancellationToken).ConfigureAwait(false)
                : await RunWithDurableLeaseAsync(
                    durableOperation,
                    token => _codec.WriteAsync(
                        outputPath,
                        manifest,
                        archiveSources,
                        recoveryPassphrase,
                        request.Overwrite,
                        stagingDirectory.Path,
                        token),
                    cancellationToken).ConfigureAwait(false);

            FileInfo archiveInfo = new(outputPath);

            if (durableOperation is not null)
            {

                await SaveCheckpointAsync(
                    durableOperation,
                    request,
                    outputPath,
                    stagingRoot,
                    stagingDirectory,
                    "archive-published",
                    $"Backup archive was verified and published with {archiveInfo.Length} encrypted bytes.",
                    CancellationToken.None,
                    snapshotReceipt,
                    archiveReceipt).ConfigureAwait(false);

                await CompleteDurableOperationAsync(
                    durableOperation,
                    CancellationToken.None).ConfigureAwait(false);

            }

            return new BackupCreateResult(
                BackupCreateStatus.Complete,
                outputPath,
                archiveInfo.Length,
                operationId,
                effectiveManifest,
                effectivePlan,
                Issues: []);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            await AbandonDurableOperationBestEffortAsync(durableOperation).ConfigureAwait(false);

            throw;

        }
        catch
        {

            await FailDurableOperationBestEffortAsync(
                durableOperation,
                "backup.create_failed").ConfigureAwait(false);

            throw;

        }
        finally
        {

            unlock?.Dispose();

            foreach (byte[] sensitive in sensitiveMemorySources)
            {

                CryptographicOperations.ZeroMemory(sensitive);

            }

            // Released only here: after the archive writer has completed or failed, never between the
            // snapshot and the last output byte.
            if (installationRead is not null)
            {

                await installationRead.DisposeAsync().ConfigureAwait(false);

            }

            _ = stagingDirectory?.TryDelete();

        }

    }

    /// <summary>
    /// Commits the snapshot-read acknowledgement, or refuses the read.
    /// </summary>
    /// <remarks>
    /// Commit failure prevents the corresponding read. Continuing past a barrier that did not commit
    /// would produce exactly the unaccounted disclosure the barrier exists to prevent.
    /// </remarks>
    private async Task<CovenantBackupDisclosureAcknowledgement?> AcknowledgeSnapshotReadAsync(
        Guid operationId,
        string outputPath,
        CancellationToken cancellationToken)
    {

        if (_covenant is not { } covenant)
        {

            return null;

        }

        Result<CovenantBackupDisclosureAcknowledgement> acknowledged = await covenant.Boundary
            .BeforeSnapshotReadAsync(
                operationId,
                BackupEffectIdentity(operationId),
                DestinationIdentity(outputPath),
                cancellationToken)
            .ConfigureAwait(false);

        return acknowledged.IsSuccess
            ? acknowledged.Value
            : throw new InvalidOperationException(
                "A protected backup could not commit its snapshot-read disclosure: "
                + acknowledged.Error.Message);

    }

    /// <summary>
    /// Commits the archive-write acknowledgement, or refuses the write.
    /// </summary>
    private async Task<CovenantBackupDisclosureAcknowledgement?> AcknowledgeArchiveWriteAsync(
        Guid operationId,
        string outputPath,
        CancellationToken cancellationToken)
    {

        if (_covenant is not { } covenant)
        {

            return null;

        }

        Result<CovenantBackupDisclosureAcknowledgement> acknowledged = await covenant.Boundary
            .BeforeArchiveWriteAsync(
                operationId,
                BackupEffectIdentity(operationId),
                DestinationIdentity(outputPath),
                cancellationToken)
            .ConfigureAwait(false);

        return acknowledged.IsSuccess
            ? acknowledged.Value
            : throw new InvalidOperationException(
                "A protected backup could not commit its archive-write disclosure: "
                + acknowledged.Error.Message);

    }

    /// <summary>
    /// The backup's own stable identity, derived from its operation rather than from any path.
    /// </summary>
    private static CovenantDigest BackupEffectIdentity(Guid operationId) =>
        new(SHA256.HashData(
        [
            .. System.Text.Encoding.ASCII.GetBytes("Arcanum.Covenant.BackupIdentity.v1"),
            0x00,
            .. operationId.ToByteArray(bigEndian: true),
        ]));

    /// <summary>
    /// An opaque commitment to where the archive is going.
    /// </summary>
    /// <remarks>
    /// A digest, never the path. Storing the destination itself would recreate inside the journal the
    /// exposure the journal exists to account for, and a receipt only has to distinguish destinations,
    /// not describe them (§10.13).
    /// </remarks>
    private static CovenantDigest DestinationIdentity(string outputPath) =>
        new(SHA256.HashData(
        [
            .. System.Text.Encoding.ASCII.GetBytes("Arcanum.Covenant.BackupDestinationIdentity.v1"),
            0x00,
            .. System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(outputPath)),
        ]));

    public Task<BackupInspectResult> InspectAsync(
        string archivePath,
        ReadOnlyMemory<char>? recoveryPassphrase,
        CancellationToken cancellationToken = default) =>
        _codec.InspectAsync(archivePath, recoveryPassphrase, cancellationToken);

    /// <summary>
    /// Verifies an archive, decrypting it into one owner-only OS temporary root rather than beside
    /// the archive or inside the installation.
    /// </summary>
    /// <remarks>
    /// The operator names the archive, so its directory is theirs, not ours: a USB stick, a synced
    /// folder, a share. Verification materializes the whole decrypted payload and every entry, and
    /// none of that may land there — nor can it, on media that cannot hold owner-only permissions,
    /// where the attempt reported a sound archive as malformed. Restore already keeps its staging
    /// local for the same reason; this gives verification, which has no destination of its own, the
    /// same footing. The per-run root is outside the guarded installation topology so an archive-only
    /// verification cannot recreate that topology during reset, and identity-owned cleanup removes
    /// the root after codec cleanup has removed its children.
    /// </remarks>
    public async Task<BackupVerifyResult> VerifyAsync(
        string archivePath,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default)
    {

        string scratchPath = Path.Combine(
            Path.GetFullPath(Path.GetTempPath()),
            "arcanum-backup-verify-" + Guid.NewGuid().ToString("N"));

        OwnedTemporaryDirectory scratch = OwnedTemporaryDirectory.Create(scratchPath);

        try
        {

            return await _codec
                .VerifyAsync(
                    archivePath,
                    recoveryPassphrase,
                    scratch.Path,
                    cancellationToken)
                .ConfigureAwait(false);

        }
        finally
        {

            _ = scratch.TryDelete();

        }

    }

    public async Task<IReadOnlyList<BackupListItem>> ListAsync(
        string? directory,
        CancellationToken cancellationToken = default)
    {

        string fullDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(directory)
                ? _paths.BackupsDirectory
                : directory);

        if (!Directory.Exists(fullDirectory))
        {

            return [];

        }

        List<BackupListItem> items = [];

        foreach (string path in Directory.EnumerateFiles(
                     fullDirectory,
                     "*" + BackupArchiveFormat.Extension,
                     SearchOption.TopDirectoryOnly))
        {

            cancellationToken.ThrowIfCancellationRequested();

            try
            {

                BackupInspectResult inspection = await _codec.InspectAsync(
                    path,
                    passphrase: null,
                    cancellationToken).ConfigureAwait(false);

                items.Add(new BackupListItem(
                    inspection.ArchivePath,
                    inspection.ArchiveBytes,
                    inspection.Header));

            }
            catch (Exception ex) when (
                ex is InvalidDataException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {

            }

        }

        return items
            .OrderByDescending(static item => item.Header.CreatedAt)
            .ThenBy(static item => item.ArchivePath, StringComparer.Ordinal)
            .ToArray();

    }

    private async Task<DatabaseUnlockMaterial> ReadDatabaseUnlockAsync()
    {

        SecretStoreReadResult result = await _secrets
            .ReadGrimoireSecretAsync()
            .ConfigureAwait(false);

        if (result.Status != SecretStoreReadStatus.Ok
            || string.IsNullOrEmpty(result.Value))
        {

            throw new InvalidDataException(
                result.Message
                    ?? "The Grimoire encryption secret is unavailable for backup.");

        }

        GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(_paths.DatabasePath);

        byte[] salt = sidecar.GetSaltBytes();

        try
        {

            string passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                result.Value,
                salt);

            _passphraseSource?.SetPassphrase(passphrase);

            return new DatabaseUnlockMaterial(result.Value, passphrase);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(salt);

        }

    }

    private async Task<DurableBackupOperation?> StartDurableOperationAsync(
        BackupCreateRequest request,
        string outputPath,
        CancellationToken cancellationToken)
    {

        if (_operationCoordinator is null || _operationStore is null)
        {

            return null;

        }

        string owner = $"{System.Environment.MachineName}:{System.Environment.ProcessId}:backup:{Guid.NewGuid():N}";

        LongRunningOperationLeaseResult lease = await _operationCoordinator.StartAsync(
            new LongRunningOperationCreateRequest(
                LongRunningOperationKinds.BackupCreate,
                LongRunningOperationRecoveryPolicy.AbandonSafely,
                $"Creating {request.Plan.Scope} backup for {Path.GetFileName(outputPath)}.",
                _timeProvider.GetUtcNow(),
                SessionId: request.Plan.SessionId),
            owner,
            TimeSpan.FromMinutes(15),
            cancellationToken).ConfigureAwait(false);

        if (!lease.Acquired)
        {

            throw new InvalidOperationException("Could not acquire the durable backup-operation lease.");

        }

        return new DurableBackupOperation(lease.Operation.Id, owner);

    }

    private async Task SaveCheckpointAsync(
        DurableBackupOperation operation,
        BackupCreateRequest request,
        string outputPath,
        string stagingRoot,
        OwnedTemporaryDirectory? stagingDirectory,
        string phase,
        string summary,
        CancellationToken cancellationToken,
        CovenantBackupDisclosureAcknowledgement? snapshotReceipt = null,
        CovenantBackupDisclosureAcknowledgement? archiveReceipt = null)
    {

        if (_operationCoordinator is null || _operationStore is null)
        {

            return;

        }

        LongRunningOperation current = await _operationStore.GetAsync(
            operation.OperationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The durable backup operation disappeared.");

        BackupOperationCheckpoint checkpoint = new(
            Version: 2,
            request.Plan.Scope,
            request.Plan.SessionId,
            [.. request.Plan.Include],
            [.. request.Plan.Exclude],
            outputPath,
            stagingRoot,
            request.Overwrite,
            phase,
            stagingDirectory?.VolumeId,
            stagingDirectory?.FileId,
            snapshotReceipt?.OperationId ?? archiveReceipt?.OperationId,
            snapshotReceipt is null ? null : Convert.ToHexString(snapshotReceipt.ReceiptDigest.Bytes),
            snapshotReceipt?.PhysicalAttemptOrdinal,
            archiveReceipt is null ? null : Convert.ToHexString(archiveReceipt.ReceiptDigest.Bytes),
            archiveReceipt?.PhysicalAttemptOrdinal);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            checkpoint,
            BackupJsonContext.Default.BackupOperationCheckpoint);

        try
        {

            bool saved = await _operationCoordinator.CheckpointAsync(
                operation.OperationId,
                operation.Owner,
                current.CheckpointVersion,
                checkpointVersion: 2,
                payload,
                outputPath,
                summary,
                cancellationToken).ConfigureAwait(false);

            if (!saved)
            {

                throw new InvalidOperationException("The durable backup checkpoint lease was lost.");

            }

        }
        finally
        {

            CryptographicOperations.ZeroMemory(payload);

        }

    }

    private async Task CompleteDurableOperationAsync(
        DurableBackupOperation operation,
        CancellationToken cancellationToken)
    {

        if (_operationCoordinator is null || _operationStore is null)
        {

            return;

        }

        LongRunningOperation current = await _operationStore.GetAsync(
            operation.OperationId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The durable backup operation disappeared.");

        bool completed = await _operationCoordinator.CompleteAsync(
            operation.OperationId,
            operation.Owner,
            current.Revision,
            cancellationToken).ConfigureAwait(false);

        if (!completed)
        {

            throw new InvalidOperationException("The durable backup completion lease was lost.");

        }

    }

    private async Task<T> RunWithDurableLeaseAsync<T>(
        DurableBackupOperation operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {

        if (_operationStore is null)
        {

            return await action(cancellationToken).ConfigureAwait(false);

        }

        DataRetentionLeaseMaintainer leaseMaintainer = new(
            (operationId, owner, now, expiresAt, token) =>
                _operationStore.RenewLeaseAsync(
                    operationId,
                    owner,
                    now,
                    expiresAt,
                    token),
            _timeProvider,
            leaseDuration: TimeSpan.FromMinutes(15),
            heartbeatInterval: TimeSpan.FromMinutes(1));

        return await leaseMaintainer.RunAsync(
            operation.OperationId,
            operation.Owner,
            action,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task AbandonDurableOperationBestEffortAsync(
        DurableBackupOperation? operation)
    {

        if (operation is null || _operationStore is null)
        {

            return;

        }

        try
        {

            LongRunningOperation? current = await _operationStore.GetAsync(
                operation.OperationId,
                CancellationToken.None).ConfigureAwait(false);

            if (current is not null)
            {

                _ = await _operationStore.TryTransitionAsync(
                    operation.OperationId,
                    current.Revision,
                    operation.Owner,
                    LongRunningOperationState.Abandoned,
                    _timeProvider.GetUtcNow(),
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);

            }

        }
        catch
        {

        }

    }

    private async Task FailDurableOperationBestEffortAsync(
        DurableBackupOperation? operation,
        string errorCode)
    {

        if (operation is null
            || _operationCoordinator is null
            || _operationStore is null)
        {

            return;

        }

        try
        {

            LongRunningOperation? current = await _operationStore.GetAsync(
                operation.OperationId,
                CancellationToken.None).ConfigureAwait(false);

            if (current is not null)
            {

                _ = await _operationCoordinator.FailAsync(
                    operation.OperationId,
                    operation.Owner,
                    current.Revision,
                    errorCode,
                    CancellationToken.None).ConfigureAwait(false);

            }

        }
        catch
        {

        }

    }

    private async Task<GeneratedSensitiveSource> BuildRecoveryMaterialAsync(
        string grimoireSecret,
        IReadOnlySet<string> requiredFileKeyIds,
        bool includeActiveFileKey,
        byte[]? masterApiKeyUtf8 = null)
    {

        BackupRecoveryKeySnapshot? fileKeys = null;

        if (requiredFileKeyIds.Count > 0 || includeActiveFileKey)
        {

            SecretStoreReadResult fileSecret = await _secrets
                .ReadFileEncryptionKeysAsync()
                .ConfigureAwait(false);

            if (fileSecret.Status == SecretStoreReadStatus.Ok
                && !string.IsNullOrEmpty(fileSecret.Value))
            {

                try
                {

                    fileKeys = BackupFileEncryptionKeyExporter.Export(
                        fileSecret.Value,
                        requiredFileKeyIds,
                        includeActiveFileKey);

                }
                catch (InvalidDataException ex)
                {

                    return GeneratedSensitiveSource.Failed(
                        new BackupVerifyIssue(
                            "backup.recovery_keys_invalid",
                            ex.Message,
                            BackupArchivePaths.PortableRecoveryKeys));

                }

            }
            else if (requiredFileKeyIds.Count > 0 || includeActiveFileKey)
            {

                return GeneratedSensitiveSource.Failed(
                    new BackupVerifyIssue(
                        "backup.recovery_keys_missing",
                        fileSecret.Message
                            ?? "File-encryption keys required for portable recovery are unavailable.",
                        BackupArchivePaths.PortableRecoveryKeys));

            }

        }

        byte[] secretBytes = Encoding.UTF8.GetBytes(grimoireSecret);

        try
        {

            using PortableBackupRecoveryMaterial recovery = new()
            {

                Version = 1,

                GrimoireEncryptionSecretUtf8 = secretBytes,

                ActiveFileEncryptionKeyId = fileKeys?.ActiveKeyId,

                FileEncryptionKeys = fileKeys?.Keys
                    .Select(static key => new PortableBackupFileKey(
                        key.KeyId,
                        key.KeyBytes.ToArray()))
                    .ToArray()
                    ?? [],

                // A copy, never the caller's buffer: this material zeroes what it holds when it is
                // disposed, and the caller's buffer is the one the standalone archive entry is still
                // waiting to be written from.
                MasterApiKeyUtf8 = masterApiKeyUtf8 is { Length: > 0 }
                    ? [.. masterApiKeyUtf8]
                    : null,

            };

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                recovery,
                BackupJsonContext.Default.PortableBackupRecoveryMaterial);

            return GeneratedSensitiveSource.Succeeded(json);

        }
        finally
        {

            fileKeys?.Dispose();

            CryptographicOperations.ZeroMemory(secretBytes);

        }

    }

    private async Task<GeneratedSensitiveSource> BuildMasterApiKeyAsync()
    {

        SecretStoreReadResult master = await _secrets
            .ReadMasterApiKeyAsync()
            .ConfigureAwait(false);

        if (master.Status != SecretStoreReadStatus.Ok
            || string.IsNullOrEmpty(master.Value))
        {

            return GeneratedSensitiveSource.Failed(
                new BackupVerifyIssue(
                    "backup.master_api_key_missing",
                    master.Message ?? "The explicitly requested master API key is unavailable.",
                    BackupArchivePaths.MasterApiKey));

        }

        return GeneratedSensitiveSource.Succeeded(
            Encoding.UTF8.GetBytes(master.Value));

    }

    private static async Task CopyProtectedFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {

        SecureFileOpenStatus status = SecureFileReader.TryOpenRegularFile(
            sourcePath,
            expectedIdentity: null,
            out FileStream? source,
            out _);

        if (status != SecureFileOpenStatus.Success || source is null)
        {

            throw new IOException("A required backup source is missing or is not an unaliased regular file.");

        }

        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("The backup staging path has no parent directory.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

        await using (source)
        await using (FileStream destination = SecureFilePermissions.CreateOwnerOnlyTempFile(destinationPath))
        {

            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

            destination.Flush(flushToDisk: true);

        }

    }

    private static async Task<BackupManifestEntry[]> BuildEntriesAsync(
        IReadOnlyList<PreparedBackupSource> files,
        CancellationToken cancellationToken)
    {

        BackupManifestEntry[] entries = new BackupManifestEntry[files.Count];

        for (int index = 0; index < files.Count; index++)
        {

            cancellationToken.ThrowIfCancellationRequested();

            PreparedBackupSource file = files[index];

            if (file.Memory is not null)
            {

                byte[] digest = SHA256.HashData(file.Memory);

                entries[index] = new BackupManifestEntry(
                    file.ArchivePath,
                    file.Memory.LongLength,
                    Convert.ToHexString(digest).ToLowerInvariant(),
                    file.Component);

                CryptographicOperations.ZeroMemory(digest);

                continue;

            }

            SecureFileOpenStatus status = SecureFileReader.TryOpenRegularFile(
                file.SourcePath!,
                file.ExpectedIdentity,
                out FileStream? stream,
                out _);

            if (status != SecureFileOpenStatus.Success || stream is null)
            {

                throw new IOException(
                    $"A backup source is missing or is not an unaliased regular file: {file.ArchivePath}");

            }

            await using (stream)
            {

                long size = stream.Length;

                if (size != file.PlannedSize)
                {

                    throw new IOException(
                        $"A backup source changed size after inventory: {file.ArchivePath}");

                }

                byte[] digest = await SHA256.HashDataAsync(
                    stream,
                    cancellationToken).ConfigureAwait(false);

                string sha256 = Convert.ToHexString(digest).ToLowerInvariant();

                if (!string.Equals(
                        sha256,
                        file.PlannedSha256,
                        StringComparison.Ordinal))
                {

                    CryptographicOperations.ZeroMemory(digest);

                    throw new IOException(
                        $"A backup source changed content after inventory: {file.ArchivePath}");

                }

                entries[index] = new BackupManifestEntry(
                    file.ArchivePath,
                    size,
                    sha256,
                    file.Component);

                CryptographicOperations.ZeroMemory(digest);

            }

        }

        return entries;

    }

    private BackupManifest BuildManifest(
        BackupPlanRequest request,
        BackupPlan plan,
        string schemaVersion,
        BackupManifestEntry[] entries)
    {

        Assembly assembly = typeof(BackupService).Assembly;

        string version = assembly.GetName().Version?.ToString() ?? "unknown";

        string build = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? version;

        return new BackupManifest(
            BackupArchiveFormat.CurrentVersion,
            version,
            build,
            schemaVersion,
            _timeProvider.GetUtcNow(),
            RuntimeInformation.RuntimeIdentifier,
            new BackupEnvelopeDescriptor(
                "PBKDF2",
                "HMAC-SHA256",
                0,
                string.Empty,
                "AES-256-GCM",
                256,
                12,
                16,
                BackupArchiveCodecOptions.DefaultChunkSize),
            request.Scope,
            request.SessionId,
            [.. request.Include.Distinct().Order()],
            [.. request.Exclude.Distinct().Order()],
            [.. plan.SecurityWarnings],
            plan.Components
                .Select(static component => new BackupManifestComponent(
                    component.Component,
                    component.Status,
                    component.Detail,
                    component.Files,
                    component.EstimatedBytes))
                .ToArray(),
            entries);

    }

    private static BackupPlan RecalculatePlan(
        BackupPlan plan,
        BackupManifestEntry[] entries)
    {

        BackupPlanComponent[] components = plan.Components
            .Select(component =>
            {

                BackupManifestEntry[] componentEntries = entries
                    .Where(entry => entry.Component == component.Component)
                    .ToArray();

                return componentEntries.Length == 0
                    ? component
                    : component with
                    {

                        Status = BackupComponentStatus.Complete,

                        Files = componentEntries.LongLength,

                        EstimatedBytes = componentEntries.Sum(static entry => entry.Size),

                    };

            })
            .ToArray();

        return plan with
        {

            Components = components,

            EstimatedFiles = entries.LongLength,

            EstimatedBytes = entries.Sum(static entry => entry.Size),

        };

    }

    private static BackupPlan MarkComponentFailed(
        BackupPlan plan,
        BackupComponent target,
        string detail) =>
        plan with
        {

            Components = plan.Components
                .Select(component => component.Component == target
                    ? component with
                    {

                        Status = BackupComponentStatus.Failed,

                        Detail = detail,

                        Files = 0,

                        EstimatedBytes = 0,

                    }
                    : component)
                .ToArray(),

        };

    private static BackupCreateResult Incomplete(
        Guid operationId,
        BackupPlan plan,
        string code,
        string message) =>
        new(
            BackupCreateStatus.Incomplete,
            ArchivePath: null,
            ArchiveBytes: 0,
            operationId,
            Manifest: null,
            plan,
            [new BackupVerifyIssue(code, message)]);

    /// <summary>
    /// Records the identity of the schema actually present in the captured snapshot. With no
    /// migration chain there is no history row to read, so the identity is a hash over
    /// <c>sqlite_master</c> (see <see cref="GrimoireSchemaIdentity"/>); archive verification
    /// recomputes it against the extracted snapshot.
    /// </summary>
    private static async Task<string> ReadSchemaVersionAsync(
        string databasePath,
        string databasePassphrase,
        CancellationToken cancellationToken)
    {

        // This opener builds its own connection string rather than going through
        // BackupRestoreDatabaseWorker, so the provider installation it would have inherited has to be
        // made here: the SQLCipher provider this project references carries no bundle and no
        // auto-initialization, and a cached read is what this costs.
        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = databasePassphrase,

                Mode = SqliteOpenMode.ReadOnly,

                Pooling = false,

            }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await GrimoireSchemaIdentity.ComputeAsync(connection, cancellationToken).ConfigureAwait(false);

    }

    private static async Task RemoveOperationFromSnapshotAsync(
        string databasePath,
        string databasePassphrase,
        Guid operationId,
        CancellationToken cancellationToken)
    {

        SqliteNativeRuntime.Instance.Initialize();

        await using SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = databasePassphrase,

                Mode = SqliteOpenMode.ReadWrite,

                Pooling = false,

            }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand table = connection.CreateCommand();

        table.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = 'LongRunningOperations'
            LIMIT 1;
            """;

        if (await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {

            return;

        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand delete = connection.CreateCommand();

        delete.Transaction = transaction;

        delete.CommandText = "DELETE FROM LongRunningOperations WHERE Id = $id;";

        delete.Parameters.AddWithValue("$id", operationId.ToString("N"));

        int deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (deleted > 1)
        {

            throw new InvalidDataException("The backup operation snapshot cleanup was not unique.");

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand check = connection.CreateCommand();

        check.CommandText = "PRAGMA quick_check;";

        object? result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
        {

            throw new InvalidDataException(
                "The Grimoire snapshot failed verification after operation-state normalization.");

        }

    }

    private string ResolveOutputPath(string? requestedPath)
    {

        string path = string.IsNullOrWhiteSpace(requestedPath)
            ? Path.Combine(
                _paths.BackupsDirectory,
                $"arcanum-{_timeProvider.GetUtcNow():yyyyMMddTHHmmssfffZ}{BackupArchiveFormat.Extension}")
            : requestedPath;

        return Path.GetFullPath(path);

    }

    private static HashSet<BackupComponent> ResolveSelectedComponents(
        BackupPlanRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        HashSet<BackupComponent> selected = request.Scope switch
        {
            BackupScope.Full =>
            [
                BackupComponent.GrimoireDatabase,
                BackupComponent.GrimoireKdfMetadata,
                BackupComponent.PortableRecoveryKeys,
                BackupComponent.Configuration,
                BackupComponent.SessionAttachments,
                BackupComponent.UploadedFiles,
                BackupComponent.BatchArtifacts,
                BackupComponent.GlobalCodex,
                BackupComponent.GlobalSpells,
                BackupComponent.McpConfiguration,
                BackupComponent.CliState,
                BackupComponent.TheForgeState,
                BackupComponent.CompendiumSettings,
                BackupComponent.CompendiumCertificates,
            ],
            BackupScope.ConfigurationAndAuthoredAssets =>
            [
                BackupComponent.Configuration,
                BackupComponent.GlobalCodex,
                BackupComponent.GlobalSpells,
                BackupComponent.McpConfiguration,
                BackupComponent.CliState,
                BackupComponent.TheForgeState,
                BackupComponent.CompendiumSettings,
                BackupComponent.CompendiumCertificates,
            ],
            BackupScope.SessionsAndMemory =>
            [
                BackupComponent.GrimoireDatabase,
                BackupComponent.GrimoireKdfMetadata,
                BackupComponent.PortableRecoveryKeys,
                BackupComponent.SessionAttachments,
                BackupComponent.UploadedFiles,
                BackupComponent.BatchArtifacts,
            ],
            BackupScope.SpecificSession =>
            [
                BackupComponent.GrimoireDatabase,
                BackupComponent.GrimoireKdfMetadata,
                BackupComponent.PortableRecoveryKeys,
                BackupComponent.SessionAttachments,
            ],
            BackupScope.MetadataOnly => [],
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };

        selected.UnionWith(request.Include);

        selected.ExceptWith(request.Exclude);

        return selected;

    }

    private sealed record PreparedBackupSource(
        BackupComponent Component,
        string ArchivePath,
        string? SourcePath,
        byte[]? Memory,
        long PlannedSize,
        string? PlannedSha256,
        FileHandleIdentity? ExpectedIdentity)
    {

        public static PreparedBackupSource FromFile(BackupInventoryFile file) =>
            new(
                file.Component,
                file.ArchivePath,
                file.SourcePath,
                Memory: null,
                file.Size,
                file.Sha256,
                new FileHandleIdentity(file.VolumeId, file.FileId));

        public static PreparedBackupSource FromMemory(
            BackupComponent component,
            string archivePath,
            byte[] memory) =>
            new(
                component,
                archivePath,
                SourcePath: null,
                memory,
                memory.LongLength,
                PlannedSha256: null,
                ExpectedIdentity: null);

        public BackupArchiveSource ToArchiveSource() =>
            Memory is null
                ? new BackupArchiveSource(ArchivePath, SourcePath!)
                : BackupArchiveSource.FromMemory(ArchivePath, Memory);

    }

    private sealed record GeneratedSensitiveSource(
        byte[]? Bytes,
        BackupVerifyIssue? Issue)
    {

        public static GeneratedSensitiveSource Succeeded(byte[] bytes) =>
            new(bytes, Issue: null);

        public static GeneratedSensitiveSource Failed(BackupVerifyIssue issue) =>
            new(Bytes: null, issue);

    }

    private sealed class DatabaseUnlockMaterial(
        string grimoireSecret,
        string databasePassphrase) : IDisposable
    {

        public string GrimoireSecret { get; private set; } = grimoireSecret;

        public string DatabasePassphrase { get; private set; } = databasePassphrase;

        public void Dispose()
        {

            GrimoireSecret = string.Empty;

            DatabasePassphrase = string.Empty;

        }

    }

    private sealed record DurableBackupOperation(
        Guid OperationId,
        string Owner);

}
