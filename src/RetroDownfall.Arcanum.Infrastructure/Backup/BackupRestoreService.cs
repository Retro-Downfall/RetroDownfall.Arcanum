using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The supported restore boundary: verify an archive completely, stage a whole generation, converge
/// it onto this build's schema and this machine's paths and secret protection, and then either
/// commit all of it or leave the installation exactly as it was found.
/// </summary>
/// <remarks>
/// The ordering here is the safety property, not an implementation detail. Everything that can
/// refuse — format, authentication, checksums, database readability, mapping validity, capacity —
/// runs before the first destructive step. Commit is two renames guarded by a filesystem journal, so
/// a process death has exactly three possible outcomes: the old tree, the new tree, or a journal
/// naming which one to finish. Never a mixture.
/// </remarks>
internal sealed class BackupRestoreService : IBackupRestoreService
{

    private readonly BackupStatePaths _paths;

    private readonly BackupArchiveCodec _codec;

    private readonly ISecretStore _secretStore;

    private readonly Func<IBackupService>? _safetyBackupFactory;

    private readonly TimeProvider _timeProvider;

    private readonly BackupRestoreServiceOptions _options;

    private readonly GrimoireSchemaInstaller _schemaInstaller;

    internal BackupRestoreService(
        BackupStatePaths paths,
        BackupArchiveCodec codec,
        ISecretStore secretStore,
        Func<IBackupService>? safetyBackupFactory,
        TimeProvider timeProvider,
        GrimoireSchemaInstaller schemaInstaller,
        BackupRestoreServiceOptions? options = null)
    {

        _paths = paths;

        _codec = codec;

        _secretStore = secretStore;

        _safetyBackupFactory = safetyBackupFactory;

        _timeProvider = timeProvider;

        _schemaInstaller = schemaInstaller
            ?? throw new ArgumentNullException(nameof(schemaInstaller));

        _options = options ?? new BackupRestoreServiceOptions();

    }

    /// <summary>
    /// Picks the key material the staged snapshot's authority fingerprint is computed from.
    /// </summary>
    /// <remarks>
    /// The master API key is the right input, because after commit this installation runs under it and
    /// the host recomputes the same digest at every start. It can legitimately be absent on a
    /// recovery machine where the key has not been persisted yet, and the restore must not fail for
    /// that; the portable recovery secret is always present at this point and yields a stable digest
    /// the next host start simply supersedes by advancing the counters.
    /// </remarks>
    private async Task<string> ResolveSchemaKeyMaterialAsync(string grimoireSecret)
    {

        string? masterApiKey = await _secretStore.GetApiKeyAsync().ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(masterApiKey) ? grimoireSecret : masterApiKey;

    }

    public async Task<BackupRestorePlan> PlanAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        using ArcanumMaintenanceLock? maintenance = ArcanumMaintenanceLock.TryAcquire(
            _paths.GrimoireDirectory);

        return await BuildPlanAsync(
            request,
            recoveryPassphrase,
            maintenance is not null,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<BackupRestoreResult> RestoreAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Guid operationId = Guid.NewGuid();

        List<BackupRestorePhaseRecord> phases = [];

        using ArcanumMaintenanceLock? maintenance = ArcanumMaintenanceLock.TryAcquire(
            _paths.GrimoireDirectory);

        BackupRestorePlan plan = await BuildPlanAsync(
            request,
            recoveryPassphrase,
            maintenance is not null,
            cancellationToken).ConfigureAwait(false);

        Record(phases, BackupRestorePhase.Authenticate, $"Archive format {plan.FormatVersion} accepted.");

        Record(phases, BackupRestorePhase.Inventory, $"{plan.Entries} entries, {plan.RestoredBytes} bytes.");

        Record(
            phases,
            BackupRestorePhase.Capacity,
            $"{plan.RequiredBytes} bytes required, {plan.AvailableBytes} bytes available.");

        if (request.DryRun)
        {

            return new BackupRestoreResult(
                plan.Blockers.Length == 0
                    ? BackupRestoreStatus.DryRunCompleted
                    : BackupRestoreStatus.Rejected,
                plan.ArchivePath,
                operationId,
                plan.ConflictMode,
                plan.DestinationRoot,
                SafetyBackupPath: null,
                plan,
                Manifest: null,
                Reconciliation: null,
                [.. phases],
                plan.Blockers);

        }

        if (plan.Blockers.Length > 0)
        {

            return Rejected(operationId, plan, phases, plan.Blockers);

        }

        if (plan.RequiresConfirmation && !request.Confirmed)
        {

            return Rejected(
                operationId,
                plan,
                phases,
                [
                    new BackupVerifyIssue(
                        "backup.restore_confirmation_required",
                        "This restore replaces the current installation. Re-run with explicit "
                        + "confirmation once a pre-restore safety backup is acceptable."),
                ]);

        }

        return await ExecuteAsync(
            request,
            recoveryPassphrase,
            operationId,
            plan,
            phases,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<BackupMigrateResult> MigrateAsync(
        BackupMigrateRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        return await BackupArchiveMigrator.MigrateAsync(
            _codec,
            request,
            recoveryPassphrase,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<BackupRestorePlan> BuildPlanAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        bool maintenanceAcquired,
        CancellationToken cancellationToken)
    {

        DateTimeOffset generatedAt = _timeProvider.GetUtcNow();

        List<BackupVerifyIssue> blockers = [];

        List<string> warnings = [];

        string archivePath = string.IsNullOrWhiteSpace(request.ArchivePath)
            ? string.Empty
            : Path.GetFullPath(request.ArchivePath);

        string destinationRoot = ResolveDestinationRoot(request, blockers);

        if (!maintenanceAcquired)
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_maintenance_unavailable",
                "Another process holds the Arcanum maintenance lock for this installation. Stop the "
                + "running host (or the other restore) and try again.",
                ArcanumMaintenanceLock.LockPathFor(_paths.GrimoireDirectory)));

        }

        if (archivePath.Length == 0 || !File.Exists(archivePath))
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_archive_missing",
                "The named backup archive does not exist.",
                request.ArchivePath));

        }

        Guid[] sessionIds = [.. (request.SessionIds ?? []).Distinct().Order()];

        if (request.ConflictMode == BackupRestoreConflictMode.ImportSelectedSessions
            && sessionIds.Length == 0)
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_sessions_required",
                "Importing selected Sessions requires at least one Session id."));

        }

        if (request.ConflictMode != BackupRestoreConflictMode.ImportSelectedSessions
            && sessionIds.Length > 0)
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_sessions_not_applicable",
                "Session ids apply only to the import-selected-sessions conflict mode."));

        }

        BackupPathRemapValidation mapping = BackupPathRemapper.Create(
            request.PathMappings ?? []);

        blockers.AddRange(mapping.Issues);

        BackupManifest? manifest = null;

        int formatVersion = 0;

        if (archivePath.Length > 0 && File.Exists(archivePath))
        {

            try
            {

                BackupInspectResult inspection = await _codec
                    .InspectAsync(archivePath, recoveryPassphrase, cancellationToken)
                    .ConfigureAwait(false);

                formatVersion = inspection.FormatVersion;

                manifest = inspection.Manifest;

            }
            catch (Exception exception) when (
                exception is System.Security.Cryptography.CryptographicException)
            {

                blockers.Add(new BackupVerifyIssue(
                    "backup.authentication_failed",
                    "The backup passphrase is wrong or authenticated archive bytes were changed. The "
                    + "current installation was not modified.",
                    archivePath));

            }
            catch (Exception exception) when (
                exception is InvalidDataException
                    or NotSupportedException
                    or EndOfStreamException
                    or IOException
                    or UnauthorizedAccessException
                    or System.Text.Json.JsonException)
            {

                formatVersion = await TryPeekFormatVersionAsync(archivePath, cancellationToken)
                    .ConfigureAwait(false);

                blockers.Add(
                    BackupRestoreFormatCatalog.Classify(formatVersion)
                    ?? new BackupVerifyIssue(
                        "backup.invalid_archive",
                        "The backup archive is malformed, incomplete, unsupported, or unreadable. The "
                        + "current installation was not modified.",
                        archivePath));

            }

        }

        if (manifest is not null
            && BackupRestoreFormatCatalog.Classify(manifest.FormatVersion) is BackupVerifyIssue format)
        {

            blockers.Add(format);

        }

        BackupComponent[] components = [];

        long entries = 0;

        long restoredBytes = 0;

        string sourceSchema = manifest?.DatabaseSchemaVersion ?? "unknown";

        if (manifest is not null)
        {

            components =
            [
                .. manifest.Components
                    .Where(static component => component.Status == BackupComponentStatus.Complete)
                    .Select(static component => component.Component)
                    .Order(),
            ];

            entries = manifest.Entries.LongLength;

            restoredBytes = manifest.Entries.Sum(static entry => entry.Size);

            foreach (BackupManifestEntry entry in manifest.Entries)
            {

                if (!BackupRestoreLayout.TryResolve(entry.Path, out BackupRestorePlacementDecision? decision))
                {

                    blockers.Add(new BackupVerifyIssue(
                        "backup.restore_unknown_entry",
                        "The archive contains an entry this build does not know how to place. A full "
                        + "restore never skips a file.",
                        entry.Path));

                    continue;

                }

                if (decision.Placement == BackupRestorePlacement.WithheldByPolicy
                    && decision.Reason is string reason)
                {

                    warnings.Add($"{entry.Path}: {reason}");

                }

            }

            if (!manifest.Entries.Any(
                    static entry => entry.Component == BackupComponent.GrimoireDatabase)
                && request.ConflictMode != BackupRestoreConflictMode.NewProfileRoot)
            {

                blockers.Add(new BackupVerifyIssue(
                    "backup.restore_database_absent",
                    "This archive carries no Grimoire snapshot, so it cannot replace an installation. "
                    + "Restore it into a new profile root instead."));

            }

            if (!manifest.Entries.Any(
                    static entry => entry.Component == BackupComponent.PortableRecoveryKeys))
            {

                blockers.Add(new BackupVerifyIssue(
                    "backup.restore_recovery_material_missing",
                    "This archive carries no portable recovery material, so its Grimoire cannot be "
                    + "unlocked on this machine.",
                    BackupArchivePaths.PortableRecoveryKeys));

            }

            warnings.AddRange(manifest.SecurityWarnings);

        }

        long displacedBytes = request.ConflictMode == BackupRestoreConflictMode.ReplaceInstallation
            ? BackupRestoreCapacityPlanner.MeasureDirectoryBytes(_paths.GrimoireDirectory)
            : 0;

        BackupRestoreCapacity capacity = BackupRestoreCapacityPlanner.Plan(
            Path.GetDirectoryName(destinationRoot) ?? destinationRoot,
            restoredBytes,
            displacedBytes,
            _options.AvailableBytesOverrideForTests);

        if (capacity.Issue is not null)
        {

            blockers.Add(capacity.Issue);

        }

        string destinationSchema = await ReadDestinationSchemaAsync(cancellationToken)
            .ConfigureAwait(false);

        bool safetyBackupPlanned =
            request.ConflictMode == BackupRestoreConflictMode.ReplaceInstallation
            && request.CreateSafetyBackup
            && _safetyBackupFactory is not null
            && File.Exists(_paths.DatabasePath);

        if (request.ConflictMode == BackupRestoreConflictMode.ReplaceInstallation
            && !safetyBackupPlanned
            && File.Exists(_paths.DatabasePath))
        {

            warnings.Add(
                "A pre-restore safety backup was declined. The displaced installation is retained in "
                + "the restore staging root until cleanup, and nowhere else.");

        }

        if (request.ConflictMode == BackupRestoreConflictMode.NewProfileRoot)
        {

            warnings.Add(
                "A new-profile restore installs data only. Local secret protection is not written for "
                + "another root, so adopt this generation with a replace-installation restore before "
                + "using it.");

        }

        if (request.RestoreMasterApiKey)
        {

            warnings.Add(
                "The archived master API key will be adopted on this machine, replacing the current key.");

        }

        return new BackupRestorePlan(
            generatedAt,
            archivePath,
            manifest?.FormatVersion ?? formatVersion,
            request.ConflictMode,
            destinationRoot,
            components,
            entries,
            restoredBytes,
            capacity.RequiredBytes,
            capacity.AvailableBytes,
            sourceSchema,
            destinationSchema,
            !string.Equals(sourceSchema, destinationSchema, StringComparison.Ordinal),
            sessionIds,
            [
                .. (request.PathMappings ?? []).Select(
                    static candidate => new BackupRestoreMappingPlan(
                        candidate.Kind,
                        candidate.From,
                        candidate.To,
                        MatchedTargets: 0)),
            ],
            UnmappedNonportablePaths: [],
            request.ConflictMode == BackupRestoreConflictMode.ReplaceInstallation
            && Directory.Exists(_paths.GrimoireDirectory),
            safetyBackupPlanned,
            [.. warnings.Distinct(StringComparer.Ordinal)],
            [.. blockers]);

    }

    private async Task<BackupRestoreResult> ExecuteAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        Guid operationId,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        CancellationToken cancellationToken)
    {

        string liveRoot = Path.GetFullPath(_paths.GrimoireDirectory);

        string stagingParent = Path.GetDirectoryName(
            request.ConflictMode == BackupRestoreConflictMode.NewProfileRoot
                ? plan.DestinationRoot
                : liveRoot)
            ?? throw new InvalidOperationException("The restore destination has no parent directory.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(stagingParent);

        string stagingPath = Path.Combine(stagingParent, BackupRestoreJournal.CreateStagingName());

        // Recorded before the root exists, and beside the live installation rather than inside it: a
        // new-profile restore stages beside its destination, where the startup sweep of the live
        // root's parent would never look for it.
        BackupRestoreStagingIndex.Add(liveRoot, stagingPath);

        OwnedTemporaryDirectory staging = OwnedTemporaryDirectory.Create(stagingPath);

        string stagedRoot = Path.Combine(staging.Path, BackupRestoreJournal.StagedDirectoryName);

        string displacedRoot = Path.Combine(staging.Path, BackupRestoreJournal.DisplacedDirectoryName);

        string workRoot = Path.Combine(staging.Path, BackupRestoreJournal.WorkDirectoryName);

        BackupRestoreJournalRecord journal = BackupRestoreJournal.Write(
            staging.Path,
            new BackupRestoreJournalRecord(
                BackupRestoreJournal.CurrentVersion,
                operationId,
                request.ConflictMode,
                BackupRestorePhase.Stage,
                liveRoot,
                stagedRoot,
                displacedRoot,
                SafetyBackupPath: null,
                plan.ArchivePath,
                staging.VolumeId,
                staging.FileId));

        CommitOutcome? commit = null;

        // An unverifiable reversal leaves the displaced installation inside staging, so the cleanup
        // below must not run: the journal plus the staging root are the operator's only recovery
        // point until BackupRestoreRecovery resolves them at the next start.
        bool retainStagingForReconciliation = false;

        BackupRestorePlan effectivePlan = plan;

        string? safetyBackupPath = null;

        BackupSecretRewrapper rewrapper = new(_secretStore);

        BackupSecretSnapshot priorSecrets = await rewrapper.CaptureAsync().ConfigureAwait(false);

        try
        {

            _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.Stage);

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(stagedRoot);

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(workRoot);

            string extractRoot = Path.Combine(workRoot, "extract");

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(extractRoot);

            BackupArchiveExtraction extraction = await _codec.ExtractAsync(
                plan.ArchivePath,
                recoveryPassphrase,
                extractRoot,
                workRoot,
                cancellationToken).ConfigureAwait(false);

            if (extraction.Manifest is null)
            {

                return Rejected(operationId, plan, phases, extraction.Issues);

            }

            BackupVerifyIssue[] placement = ComposeStagedTree(
                extraction.Manifest,
                extractRoot,
                stagedRoot);

            if (placement.Length > 0)
            {

                return Rejected(operationId, plan, phases, placement);

            }

            Record(
                phases,
                BackupRestorePhase.Stage,
                $"Staged {extraction.Entries} entries beneath a protected root.");

            StageResult staged = await PrepareStagedGenerationAsync(
                request,
                extractRoot,
                stagedRoot,
                plan,
                phases,
                cancellationToken).ConfigureAwait(false);

            if (staged.Issues.Length > 0)
            {

                return Rejected(operationId, plan, phases, staged.Issues);

            }

            effectivePlan = staged.Plan;

            if (request.ConflictMode == BackupRestoreConflictMode.ImportSelectedSessions)
            {

                return await ImportSelectedSessionsAsync(
                    request,
                    operationId,
                    effectivePlan,
                    phases,
                    extractRoot,
                    stagedRoot,
                    cancellationToken).ConfigureAwait(false);

            }

            if (effectivePlan.SafetyBackupPlanned)
            {

                _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.SafetyPoint);

                BackupCreateResult? safety = await CreateSafetyBackupAsync(
                    recoveryPassphrase,
                    cancellationToken).ConfigureAwait(false);

                // The safety backup is the recovery point the operator asked for, so a create that
                // did not complete stops the restore here — before the commit displaces the live
                // installation, which the cleanup below then removes. Nothing is destroyed yet.
                if (safety is not { Status: BackupCreateStatus.Complete, ArchivePath: { } archived }
                    || string.IsNullOrWhiteSpace(archived))
                {

                    Record(
                        phases,
                        BackupRestorePhase.SafetyPoint,
                        "The pre-restore safety backup did not complete; the restore stopped before "
                        + "any destructive step.");

                    List<BackupVerifyIssue> safetyIssues =
                    [
                        new BackupVerifyIssue(
                            "backup.restore_safety_backup_failed",
                            "The requested pre-restore safety backup did not complete, so nothing was "
                            + "displaced and the current installation is unchanged. Resolve the backup "
                            + "failure, or re-run with the safety backup declined to accept the "
                            + "displaced installation as the only recovery point."),
                    ];

                    if (safety is not null)
                    {

                        safetyIssues.AddRange(safety.Issues);

                    }

                    return Rejected(operationId, effectivePlan, phases, safetyIssues);

                }

                safetyBackupPath = archived;

                journal = BackupRestoreJournal.Write(
                    staging.Path,
                    journal with { SafetyBackupPath = safetyBackupPath });

                Record(
                    phases,
                    BackupRestorePhase.SafetyPoint,
                    $"Pre-restore safety backup written to {safetyBackupPath}.");

            }

            _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.Commit);

            journal = BackupRestoreJournal.Advance(staging.Path, journal, BackupRestorePhase.Commit);

            commit = Commit(
                request.ConflictMode,
                liveRoot,
                effectivePlan.DestinationRoot,
                stagedRoot,
                displacedRoot);

            if (!commit.Succeeded)
            {

                if (commit.Reversal is { Restored: false } commitReversal)
                {

                    retainStagingForReconciliation = true;

                    RetainForReconciliation(staging.Path, journal, phases);

                    return ReversalIncomplete(
                        operationId,
                        effectivePlan,
                        phases,
                        safetyBackupPath,
                        staging.Path,
                        commitReversal);

                }

                return RolledBack(
                    operationId,
                    effectivePlan,
                    phases,
                    safetyBackupPath,
                    commit.Issue!);

            }

            Record(
                phases,
                BackupRestorePhase.Commit,
                $"Committed the restored generation to {effectivePlan.DestinationRoot}.");

            if (request.ConflictMode == BackupRestoreConflictMode.ReplaceInstallation)
            {

                BackupSecretRewrapResult rewrap = await rewrapper
                    .RewrapAsync(
                        Path.Combine(
                            extractRoot,
                            BackupArchivePaths.PortableRecoveryKeys.Replace('/', Path.DirectorySeparatorChar)),
                        request.RestoreMasterApiKey,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!rewrap.GrimoireSecretWritten)
                {

                    ReversalOutcome reversal = Reverse(liveRoot, stagedRoot, displacedRoot);

                    await rewrapper.RestoreAsync(priorSecrets).ConfigureAwait(false);

                    commit = null;

                    if (!reversal.Restored)
                    {

                        retainStagingForReconciliation = true;

                        RetainForReconciliation(staging.Path, journal, phases);

                        return ReversalIncomplete(
                            operationId,
                            effectivePlan,
                            phases,
                            safetyBackupPath,
                            staging.Path,
                            reversal);

                    }

                    return RolledBack(
                        operationId,
                        effectivePlan,
                        phases,
                        safetyBackupPath,
                        rewrap.Issues.FirstOrDefault()
                        ?? new BackupVerifyIssue(
                            "backup.restore_rewrap_failed",
                            "Local secret protection could not be rebuilt; the prior installation was restored."));

                }

                Record(
                    phases,
                    BackupRestorePhase.RewrapSecrets,
                    $"Rebuilt local protection for the Grimoire secret and {rewrap.FileEncryptionKeysWritten} "
                    + "file-encryption keys.");

                effectivePlan = effectivePlan with
                {

                    Warnings =
                    [
                        .. effectivePlan.Warnings
                            .Concat(rewrap.Issues.Select(static issue => issue.Message))
                            .Distinct(StringComparer.Ordinal),
                    ],

                };

            }

            journal = BackupRestoreJournal.Advance(staging.Path, journal, BackupRestorePhase.Reconcile);

            _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.Reconcile);

            BackupRestoreReconciliation reconciliation = await ReconcileAsync(
                request,
                effectivePlan.DestinationRoot,
                staged.GrimoireSecret,
                staged.EmbeddingsRebuilt,
                staged.PendingOperationsCleared,
                cancellationToken).ConfigureAwait(false);

            Record(
                phases,
                BackupRestorePhase.Reconcile,
                $"{reconciliation.Attachments} attachments, {reconciliation.UploadedFiles} uploaded files, "
                + $"{reconciliation.BatchFiles} batches, {reconciliation.StaleAttachmentSources} stale sources.");

            journal = BackupRestoreJournal.Advance(staging.Path, journal, BackupRestorePhase.Cleanup);

            Record(phases, BackupRestorePhase.Cleanup, "Removed protected restore staging.");

            return new BackupRestoreResult(
                reconciliation.Issues.Length == 0
                    ? BackupRestoreStatus.Completed
                    : BackupRestoreStatus.ReconciliationRequired,
                effectivePlan.ArchivePath,
                operationId,
                request.ConflictMode,
                effectivePlan.DestinationRoot,
                safetyBackupPath,
                effectivePlan,
                extraction.Manifest,
                reconciliation,
                [.. phases],
                []);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            if (commit is { Succeeded: true })
            {

                ReversalOutcome reversal = Reverse(liveRoot, stagedRoot, displacedRoot);

                await rewrapper.RestoreAsync(priorSecrets).ConfigureAwait(false);

                if (!reversal.Restored)
                {

                    retainStagingForReconciliation = true;

                    RetainForReconciliation(staging.Path, journal, phases);

                }

            }

            throw;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException
                or SqliteException
                or System.Security.Cryptography.CryptographicException
                or InvalidOperationException)
        {

            if (commit is { Succeeded: true })
            {

                ReversalOutcome reversal = Reverse(liveRoot, stagedRoot, displacedRoot);

                await rewrapper.RestoreAsync(priorSecrets).ConfigureAwait(false);

                if (!reversal.Restored)
                {

                    retainStagingForReconciliation = true;

                    RetainForReconciliation(staging.Path, journal, phases);

                    return ReversalIncomplete(
                        operationId,
                        effectivePlan,
                        phases,
                        safetyBackupPath,
                        staging.Path,
                        reversal);

                }

                return RolledBack(
                    operationId,
                    effectivePlan,
                    phases,
                    safetyBackupPath,
                    new BackupVerifyIssue(
                        "backup.restore_commit_failed",
                        "The restore failed after commit and the prior installation was returned to "
                        + "its original state. Diagnostics: " + exception.GetType().Name));

            }

            return Rejected(
                operationId,
                effectivePlan,
                phases,
                [
                    new BackupVerifyIssue(
                        "backup.restore_failed",
                        "The restore failed before any destructive step; the current installation is "
                        + "unchanged. Diagnostics: " + exception.GetType().Name),
                ]);

        }
        finally
        {

            if (!retainStagingForReconciliation)
            {

                BackupRestoreJournal.Delete(staging.Path);

                _ = staging.TryDelete();

                BackupRestoreStagingIndex.Remove(liveRoot, staging.Path);

            }

        }

    }

    /// <summary>
    /// Rewinds a retained journal to <see cref="BackupRestorePhase.Commit"/> so the staging root is
    /// resolved from filesystem evidence at the next start.
    /// </summary>
    /// <remarks>
    /// A journal left at a later phase reads to <see cref="BackupRestoreRecovery"/> as "the commit
    /// had already completed; only cleanup remained", which discards the staging root — and with it
    /// the displaced installation the reversal failed to put back.
    /// </remarks>
    private static void RetainForReconciliation(
        string stagingRoot,
        BackupRestoreJournalRecord journal,
        List<BackupRestorePhaseRecord> phases)
    {

        _ = BackupRestoreJournal.Advance(stagingRoot, journal, BackupRestorePhase.Commit);

        Record(
            phases,
            BackupRestorePhase.Cleanup,
            "The reversal could not be verified, so the restore journal and the displaced "
            + $"installation were retained under {stagingRoot} for reconciliation at the next start.");

    }

    private async Task<StageResult> PrepareStagedGenerationAsync(
        BackupRestoreRequest request,
        string extractRoot,
        string stagedRoot,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        CancellationToken cancellationToken)
    {

        string recoveryPath = Path.Combine(
            extractRoot,
            BackupArchivePaths.PortableRecoveryKeys.Replace('/', Path.DirectorySeparatorChar));

        if (!BackupPortableRecoveryReader.TryReadGrimoireSecret(
                recoveryPath,
                out string? grimoireSecret))
        {

            return StageResult.Failed(
                new BackupVerifyIssue(
                    "backup.restore_recovery_material_invalid",
                    "The portable recovery material could not be read, so the staged Grimoire cannot "
                    + "be opened.",
                    BackupArchivePaths.PortableRecoveryKeys));

        }

        string stagedDatabase = Path.Combine(stagedRoot, "arcanum.db");

        if (!File.Exists(stagedDatabase))
        {

            return new StageResult(plan, grimoireSecret, 0, 0, []);

        }

        _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.Migrate);

        await using SqliteConnection connection = await BackupRestoreDatabaseWorker
            .OpenAsync(stagedDatabase, grimoireSecret, readOnly: false, cancellationToken)
            .ConfigureAwait(false);

        string beforeSchema = await BackupRestoreDatabaseWorker
            .ReadSchemaIdentityAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        GrimoireSchemaInitializationContext schemaContext = await BackupRestoreDatabaseWorker
            .ResolveInitializationContextAsync(
                connection,
                await ResolveSchemaKeyMaterialAsync(grimoireSecret).ConfigureAwait(false),
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        // The tier results are deliberately not folded into the restore outcome: convergence of the
        // staged snapshot is judged by the schema identity recorded below, and a Covenant tier that
        // reports unavailable here is republished by the host at its own next bootstrap.
        _ = await BackupRestoreDatabaseWorker
            .MigrateAsync(
                connection,
                _schemaInstaller,
                _options.EmbeddingDimensions,
                schemaContext,
                cancellationToken)
            .ConfigureAwait(false);

        string afterSchema = await BackupRestoreDatabaseWorker
            .ReadSchemaIdentityAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        Record(
            phases,
            BackupRestorePhase.Migrate,
            string.Equals(beforeSchema, afterSchema, StringComparison.Ordinal)
                ? "The snapshot already matches this build's declarative schema."
                : $"Converged {beforeSchema} to {afterSchema} through the authoritative schema installer.");

        _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.RemapPaths);

        BackupPathRemapper remapper = BackupPathRemapper
            .Create(request.PathMappings ?? [])
            .Remapper!;

        BackupRestoreRemapOutcome remap = await BackupRestoreDatabaseWorker
            .RemapAsync(connection, remapper, cancellationToken)
            .ConfigureAwait(false);

        BackupRestoreConfigurationOutcome configuration = await BackupRestoreConfigurationWriter
            .ApplyAsync(
                Path.Combine(stagedRoot, "arcanum.json"),
                remapper,
                cancellationToken)
            .ConfigureAwait(false);

        long stale = await BackupRestoreDatabaseWorker
            .MarkAttachmentSourcesStaleAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        long pending = await BackupRestoreDatabaseWorker
            .ClearPendingOperationsAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        long embeddings = await BackupRestoreDatabaseWorker
            .DropMismatchedEmbeddingsAsync(connection, _options.EmbeddingDimensions, cancellationToken)
            .ConfigureAwait(false);

        Record(
            phases,
            BackupRestorePhase.RemapPaths,
            $"Rewrote {remap.MatchesByKind.Values.Sum() + configuration.RemappedValues} recorded paths; "
            + $"{remap.UnmappedNonportablePaths.Count} remain machine-specific; "
            + $"{stale} attachment sources are stale until rebound.");

        _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.Validate);

        BackupVerifyIssue[] validation = await ValidateStagedGenerationAsync(
            connection,
            stagedRoot,
            cancellationToken).ConfigureAwait(false);

        if (validation.Length > 0)
        {

            return StageResult.Failed(validation[0]);

        }

        Record(
            phases,
            BackupRestorePhase.Validate,
            "Every referenced attachment payload is present in the staged generation.");

        return new StageResult(
            plan with
            {

                DestinationSchemaIdentity = afterSchema,

                SchemaMigrationRequired = !string.Equals(
                    beforeSchema,
                    afterSchema,
                    StringComparison.Ordinal),

                PathMappings =
                [
                    .. plan.PathMappings.Select(candidate => candidate with
                    {

                        MatchedTargets = remap.MatchesByKind.TryGetValue(candidate.Kind, out long matched)
                            ? matched
                            : 0,

                    }),
                ],

                UnmappedNonportablePaths = [.. remap.UnmappedNonportablePaths],

                Warnings =
                [
                    .. plan.Warnings
                        .Concat(configuration.Warnings)
                        .Distinct(StringComparer.Ordinal),
                ],

            },
            grimoireSecret,
            embeddings,
            pending,
            []);

    }

    /// <summary>
    /// Proves the staged tree is self-consistent before commit: every attachment row the restored
    /// database points at must have arrived with it.
    /// </summary>
    private static async Task<BackupVerifyIssue[]> ValidateStagedGenerationAsync(
        SqliteConnection connection,
        string stagedRoot,
        CancellationToken cancellationToken)
    {

        IReadOnlyList<string> referenced = await BackupRestoreDatabaseWorker
            .ReadReferencedAttachmentPathsAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        string attachmentsRoot = Path.Combine(stagedRoot, "attachments");

        List<BackupVerifyIssue> issues = [];

        foreach (string relative in referenced)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string candidate = Path.GetFullPath(
                Path.Combine(attachmentsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

            if (!candidate.StartsWith(
                    attachmentsRoot + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_attachment_escapes",
                    "A restored attachment row points outside the attachment root.",
                    relative));

                break;

            }

            if (!File.Exists(candidate))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_attachment_missing",
                    "The restored database references attachment bytes the archive does not carry. A "
                    + "full restore never silently skips a file.",
                    relative));

                break;

            }

        }

        return [.. issues];

    }

    /// <summary>
    /// Lays every archive entry down under the staged root using the closed layout table. An entry
    /// the table does not recognize aborts staging rather than being dropped.
    /// </summary>
    private static BackupVerifyIssue[] ComposeStagedTree(
        BackupManifest manifest,
        string extractRoot,
        string stagedRoot)
    {

        List<BackupVerifyIssue> issues = [];

        foreach (BackupManifestEntry entry in manifest.Entries)
        {

            if (!BackupRestoreLayout.TryResolve(entry.Path, out BackupRestorePlacementDecision? decision))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_unknown_entry",
                    "The archive contains an entry this build does not know how to place.",
                    entry.Path));

                break;

            }

            if (decision.Placement != BackupRestorePlacement.Install
                || decision.RelativeDestination is null)
            {

                continue;

            }

            string source = Path.Combine(
                extractRoot,
                entry.Path.Replace('/', Path.DirectorySeparatorChar));

            string destination = Path.Combine(
                stagedRoot,
                decision.RelativeDestination.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(source))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.restore_entry_missing",
                    "An authenticated archive entry did not materialize during staging.",
                    entry.Path));

                break;

            }

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(
                Path.GetDirectoryName(destination)!);

            File.Copy(source, destination, overwrite: true);

            SecureFilePermissions.ApplyOwnerOnlyFile(destination);

        }

        return [.. issues];

    }

    /// <summary>
    /// The two-rename commit. Between the renames the journal names both roots, so an interrupted
    /// commit is always resolvable to exactly one complete tree.
    /// </summary>
    private CommitOutcome Commit(
        BackupRestoreConflictMode mode,
        string liveRoot,
        string destinationRoot,
        string stagedRoot,
        string displacedRoot)
    {

        try
        {

            if (mode == BackupRestoreConflictMode.NewProfileRoot)
            {

                if (Directory.Exists(destinationRoot))
                {

                    Directory.Delete(destinationRoot);

                }

                Directory.Move(stagedRoot, destinationRoot);

                return new CommitOutcome(true, Issue: null);

            }

            bool displaced = Directory.Exists(liveRoot);

            if (displaced)
            {

                Directory.Move(liveRoot, displacedRoot);

            }

            Directory.Move(stagedRoot, liveRoot);

            if (displaced)
            {

                PreserveMachineLocalEntries(displacedRoot, liveRoot);

            }

            return new CommitOutcome(true, Issue: null);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            // A rename failed partway. Undo whatever landed here and now, rather than leaving the
            // live root missing for the journal to repair at the next start.
            ReversalOutcome reversal = Reverse(liveRoot, stagedRoot, displacedRoot);

            return new CommitOutcome(
                false,
                new BackupVerifyIssue(
                    "backup.restore_commit_failed",
                    "The restored generation could not be committed atomically; the prior installation "
                    + "was returned to its original state."),
                reversal);

        }

    }

    /// <summary>
    /// Undoes <see cref="Commit"/>, driven by what the filesystem actually shows rather than by how
    /// far the code believes it got — the same evidence <see cref="BackupRestoreRecovery"/> uses
    /// after a process death, so an in-process reversal and a restart reversal agree.
    /// </summary>
    /// <remarks>
    /// A reversal that cannot be verified is never reported as clean. The displaced tree is the
    /// operator's only remaining copy of the installation, so an incomplete reversal keeps the
    /// journal and the staging root for <see cref="BackupRestoreRecovery"/> to resolve at the next
    /// start rather than tidying away the evidence.
    /// </remarks>
    private ReversalOutcome Reverse(
        string liveRoot,
        string stagedRoot,
        string displacedRoot)
    {

        try
        {

            bool stagedStillPresent = Directory.Exists(stagedRoot);

            // Filesystem evidence, not a success flag: any preserved entry that already reached the
            // new live tree is carried back, even when the commit reported failure partway through
            // preserving. Otherwise a half-preserved key ring rides into staging and is deleted.
            if (Directory.Exists(displacedRoot))
            {

                PreserveMachineLocalEntries(liveRoot, displacedRoot);

            }

            _options.BeforeReversalRenameForTests?.Invoke();

            if (!stagedStillPresent && Directory.Exists(liveRoot))
            {

                Directory.Move(liveRoot, stagedRoot);

            }

            if (!Directory.Exists(liveRoot) && Directory.Exists(displacedRoot))
            {

                Directory.Move(displacedRoot, liveRoot);

            }

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return new ReversalOutcome(Restored: false, exception.GetType().Name);

        }

        // The displaced tree is gone only when every part of it landed back in the live root. While
        // it still exists the reversal is incomplete however far the code believes it got.
        return Directory.Exists(displacedRoot)
            ? new ReversalOutcome(Restored: false, "the displaced installation is still in staging")
            : new ReversalOutcome(Restored: true, Diagnostics: null);

    }

    /// <summary>
    /// Carries the destination's own non-portable state across the swap: the Data Protection key
    /// ring that local secret wrapping depends on, and the backup archives — including the one this
    /// restore is reading from.
    /// </summary>
    private void PreserveMachineLocalEntries(string from, string to)
    {

        foreach (string name in BackupRestoreLayout.PreservedFromCurrentInstallation)
        {

            string source = Path.Combine(from, name);

            string destination = Path.Combine(to, name);

            if (!Directory.Exists(source) || Directory.Exists(destination))
            {

                continue;

            }

            _options.BeforePreservedEntryMoveForTests?.Invoke(name);

            Directory.Move(source, destination);

        }

    }

    private async Task<BackupRestoreResult> ImportSelectedSessionsAsync(
        BackupRestoreRequest request,
        Guid operationId,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        string extractRoot,
        string stagedRoot,
        CancellationToken cancellationToken)
    {

        SecretStoreReadResult destinationSecret = await _secretStore
            .GetGrimoireEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);

        if (destinationSecret.Status != SecretStoreReadStatus.Ok
            || string.IsNullOrEmpty(destinationSecret.Value)
            || !File.Exists(_paths.DatabasePath))
        {

            return Rejected(
                operationId,
                plan,
                phases,
                [
                    new BackupVerifyIssue(
                        "backup.restore_import_destination_unavailable",
                        "Importing Sessions requires an initialized local Grimoire this machine can open."),
                ]);

        }

        BackupSecretRewrapResult merged = await new BackupSecretRewrapper(_secretStore)
            .MergeFileEncryptionKeysAsync(
                Path.Combine(
                    extractRoot,
                    BackupArchivePaths.PortableRecoveryKeys.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken)
            .ConfigureAwait(false);

        if (merged.Issues.Length > 0)
        {

            return Rejected(operationId, plan, phases, merged.Issues);

        }

        _options.BeforePhaseForTests?.Invoke(BackupRestorePhase.Commit);

        BackupSessionImportResult import = await BackupSessionImporter.ImportAsync(
            Path.Combine(stagedRoot, "arcanum.db"),
            _paths.DatabasePath,
            plan.SelectedSessionIds,
            Path.Combine(stagedRoot, "attachments"),
            _paths.AttachmentsDirectory,
            destinationSecret.Value,
            ReadStagedSecret(extractRoot),
            cancellationToken).ConfigureAwait(false);

        if (import.Issues.Length > 0)
        {

            return Rejected(operationId, plan, phases, import.Issues);

        }

        Record(
            phases,
            BackupRestorePhase.Commit,
            $"Imported {import.Sessions} Sessions, {import.Entries} entries, and "
            + $"{import.Attachments} attachments; {import.RemappedIds} ids were remapped around collisions.");

        BackupRestoreReconciliation reconciliation = new(
            import.Attachments,
            import.Attachments,
            UploadedFiles: 0,
            BatchFiles: 0,
            EmbeddingsRebuilt: 0,
            PendingOperationsCleared: 0,
            Issues: []);

        Record(
            phases,
            BackupRestorePhase.Reconcile,
            $"{import.DeduplicatedBlobs} attachment payloads already present were deduplicated.");

        Record(phases, BackupRestorePhase.Cleanup, "Removed protected restore staging.");

        return new BackupRestoreResult(
            BackupRestoreStatus.Completed,
            plan.ArchivePath,
            operationId,
            request.ConflictMode,
            plan.DestinationRoot,
            SafetyBackupPath: null,
            plan,
            Manifest: null,
            reconciliation,
            [.. phases],
            []);

    }

    private static string ReadStagedSecret(string extractRoot) =>
        BackupPortableRecoveryReader.TryReadGrimoireSecret(
            Path.Combine(
                extractRoot,
                BackupArchivePaths.PortableRecoveryKeys.Replace('/', Path.DirectorySeparatorChar)),
            out string? secret)
            ? secret
            : string.Empty;

    private async Task<BackupRestoreReconciliation> ReconcileAsync(
        BackupRestoreRequest request,
        string destinationRoot,
        string grimoireSecret,
        long embeddingsRebuilt,
        long pendingCleared,
        CancellationToken cancellationToken)
    {

        string databasePath = Path.Combine(destinationRoot, "arcanum.db");

        if (request.ConflictMode == BackupRestoreConflictMode.NewProfileRoot
            || !File.Exists(databasePath))
        {

            return new BackupRestoreReconciliation(0, 0, 0, 0, embeddingsRebuilt, pendingCleared, []);

        }

        try
        {

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                .OpenAsync(databasePath, grimoireSecret, readOnly: true, cancellationToken)
                .ConfigureAwait(false);

            BackupRestoreDatabaseReconciliation counts = await BackupRestoreDatabaseWorker
                .ReconcileAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            return new BackupRestoreReconciliation(
                counts.Attachments,
                counts.StaleAttachmentSources,
                counts.UploadedFiles,
                counts.BatchFiles,
                embeddingsRebuilt,
                pendingCleared,
                []);

        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException)
        {

            return new BackupRestoreReconciliation(
                0,
                0,
                0,
                0,
                embeddingsRebuilt,
                pendingCleared,
                [
                    "The restored generation is committed but could not be re-opened for "
                    + "post-commit reconciliation.",
                ]);

        }

    }

    /// <summary>
    /// Produces the pre-restore safety backup, returning the whole create result — status and
    /// issues — so the caller can refuse the restore on anything short of a complete archive rather
    /// than commit against a recovery point that was never written.
    /// </summary>
    private async Task<BackupCreateResult?> CreateSafetyBackupAsync(
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken)
    {

        if (_safetyBackupFactory is null)
        {

            return null;

        }

        string path = Path.Combine(
            _paths.BackupsDirectory,
            $"arcanum-pre-restore-{_timeProvider.GetUtcNow():yyyyMMddTHHmmssfffZ}{BackupArchiveFormat.Extension}");

        return await _safetyBackupFactory()
            .CreateAsync(
                new BackupCreateRequest(
                    new BackupPlanRequest(BackupScope.Full, SessionId: null, Include: [], Exclude: []),
                    path,
                    Overwrite: false),
                recoveryPassphrase,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private string ResolveDestinationRoot(
        BackupRestoreRequest request,
        List<BackupVerifyIssue> blockers)
    {

        if (request.ConflictMode != BackupRestoreConflictMode.NewProfileRoot)
        {

            if (!string.IsNullOrWhiteSpace(request.DestinationRoot))
            {

                blockers.Add(new BackupVerifyIssue(
                    "backup.restore_destination_not_applicable",
                    "A destination root applies only to the new-profile-root conflict mode."));

            }

            return Path.GetFullPath(_paths.GrimoireDirectory);

        }

        if (string.IsNullOrWhiteSpace(request.DestinationRoot)
            || !Path.IsPathFullyQualified(request.DestinationRoot))
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_destination_required",
                "Restoring into a new profile root requires a fully qualified destination path."));

            return Path.GetFullPath(_paths.GrimoireDirectory);

        }

        string destination = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.DestinationRoot));

        if (File.Exists(destination)
            || (Directory.Exists(destination)
                && Directory.EnumerateFileSystemEntries(destination).Any()))
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_destination_not_empty",
                "The new profile root must be an empty or absent directory.",
                destination));

        }

        if (string.Equals(
                destination,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.GrimoireDirectory)),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_destination_is_current",
                "The new profile root may not be the current installation root. Use the "
                + "replace-installation conflict mode instead.",
                destination));

        }

        return destination;

    }

    private async Task<string> ReadDestinationSchemaAsync(CancellationToken cancellationToken)
    {

        if (!File.Exists(_paths.DatabasePath))
        {

            return "absent";

        }

        try
        {

            SecretStoreReadResult secret = await _secretStore
                .GetGrimoireEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            if (secret.Status != SecretStoreReadStatus.Ok || string.IsNullOrEmpty(secret.Value))
            {

                return "unreadable";

            }

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                .OpenAsync(_paths.DatabasePath, secret.Value, readOnly: true, cancellationToken)
                .ConfigureAwait(false);

            return await BackupRestoreDatabaseWorker
                .ReadSchemaIdentityAsync(connection, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
        {

            return "unreadable";

        }

    }

    private static async Task<int> TryPeekFormatVersionAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {

        try
        {

            BackupInspectResult inspection = await new BackupArchiveCodec()
                .InspectAsync(archivePath, passphrase: null, cancellationToken)
                .ConfigureAwait(false);

            return inspection.FormatVersion;

        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or NotSupportedException
                or EndOfStreamException
                or IOException
                or UnauthorizedAccessException)
        {

            return await BackupArchiveHeaderPeek
                .TryReadFormatVersionAsync(archivePath, cancellationToken)
                .ConfigureAwait(false);

        }

    }

    private static void Record(
        List<BackupRestorePhaseRecord> phases,
        BackupRestorePhase phase,
        string detail) =>
        phases.Add(new BackupRestorePhaseRecord(phase, detail));

    private static BackupRestoreResult Rejected(
        Guid operationId,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        IReadOnlyList<BackupVerifyIssue> issues) =>
        new(
            BackupRestoreStatus.Rejected,
            plan.ArchivePath,
            operationId,
            plan.ConflictMode,
            plan.DestinationRoot,
            SafetyBackupPath: null,
            plan,
            Manifest: null,
            Reconciliation: null,
            [.. phases],
            [.. issues]);

    /// <summary>
    /// The outcome when a post-commit reversal could not be verified. Nothing is deleted: the
    /// journal and the staging root — which still holds the displaced installation — are retained so
    /// <see cref="BackupRestoreRecovery"/> can resolve them from filesystem evidence at the next
    /// start, and the operator is told where they are instead of being told the rollback was clean.
    /// </summary>
    private static BackupRestoreResult ReversalIncomplete(
        Guid operationId,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        string? safetyBackupPath,
        string stagingRoot,
        ReversalOutcome reversal) =>
        new(
            BackupRestoreStatus.ReconciliationRequired,
            plan.ArchivePath,
            operationId,
            plan.ConflictMode,
            plan.DestinationRoot,
            safetyBackupPath,
            plan,
            Manifest: null,
            Reconciliation: null,
            [.. phases],
            [
                new BackupVerifyIssue(
                    "backup.restore_reversal_incomplete",
                    "The restore failed after commit and the prior installation could not be verifiably "
                    + "returned to its original state. Nothing was deleted: the restore journal and the "
                    + "displaced installation are preserved under " + stagingRoot
                    + " and are resolved at the next start. Diagnostics: "
                    + (reversal.Diagnostics ?? "the reversal did not complete")),
            ]);

    private static BackupRestoreResult RolledBack(
        Guid operationId,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        string? safetyBackupPath,
        BackupVerifyIssue issue) =>
        new(
            BackupRestoreStatus.RolledBack,
            plan.ArchivePath,
            operationId,
            plan.ConflictMode,
            plan.DestinationRoot,
            safetyBackupPath,
            plan,
            Manifest: null,
            Reconciliation: null,
            [.. phases],
            [issue]);

    private sealed record CommitOutcome(
        bool Succeeded,
        BackupVerifyIssue? Issue,
        ReversalOutcome? Reversal = null);

    /// <summary>
    /// What a reversal could actually prove about the filesystem afterwards. <c>Restored</c> is only
    /// true when the displaced installation is verifiably back in the live root.
    /// </summary>
    private sealed record ReversalOutcome(
        bool Restored,
        string? Diagnostics);

    private sealed record StageResult(
        BackupRestorePlan Plan,
        string GrimoireSecret,
        long EmbeddingsRebuilt,
        long PendingOperationsCleared,
        BackupVerifyIssue[] Issues)
    {

        public static StageResult Failed(BackupVerifyIssue issue) =>
            new(
                null!,
                string.Empty,
                0,
                0,
                [issue]);

    }

}
