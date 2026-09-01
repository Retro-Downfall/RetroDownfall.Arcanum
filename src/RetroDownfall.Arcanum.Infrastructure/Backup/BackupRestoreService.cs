using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

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

    private readonly InstallationMaintenanceCoordination? _maintenanceCoordination;

    /// <summary>
    /// The Covenant arm of a full restore, or <see langword="null"/> on an installation that never
    /// enabled the gate.
    /// </summary>
    private readonly BackupRestoreCovenantCoordinator? _covenant;

    internal BackupRestoreService(
        BackupStatePaths paths,
        BackupArchiveCodec codec,
        ISecretStore secretStore,
        Func<IBackupService>? safetyBackupFactory,
        TimeProvider timeProvider,
        GrimoireSchemaInstaller schemaInstaller,
        BackupRestoreServiceOptions? options = null,
        InstallationMaintenanceCoordination? maintenanceCoordination = null)
    {

        _paths = paths;

        _codec = codec;

        _secretStore = secretStore;

        _safetyBackupFactory = safetyBackupFactory;

        _timeProvider = timeProvider;

        _schemaInstaller = schemaInstaller
            ?? throw new ArgumentNullException(nameof(schemaInstaller));

        _options = options ?? new BackupRestoreServiceOptions();

        _maintenanceCoordination = maintenanceCoordination;

        _covenant = _options.RestoreStaging is { } staging
            ? new BackupRestoreCovenantCoordinator(
                staging,
                CovenantSqliteConnectionInitializer.Instance,
                _timeProvider)
            : null;

    }

    /// <summary>
    /// Whether this restore reconciles protected state, or is the pre-Covenant path it has always been.
    /// </summary>
    /// <remarks>
    /// Replacement only. A new-profile restore installs data beside the installation without displacing
    /// it: there is no live root to close admission on, no destination authority to join into, and no
    /// destination marker to clean up — and the plan already warns that such a generation has to be
    /// adopted through a replace-installation restore before it is used.
    /// </remarks>
    private bool ReconcilesProtectedState(BackupRestoreRequest request) =>
        _covenant is not null
        && request.ConflictMode == BackupRestoreConflictMode.ReplaceInstallation;

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

        ArcanumMaintenanceLockAcquisitionResult acquisition =
            ArcanumMaintenanceLock.AcquireDetailed(_paths.GrimoireDirectory);

        using ArcanumMaintenanceLock? maintenance = acquisition.Lock;

        return await BuildPlanAsync(
            request,
            recoveryPassphrase,
            acquisition.Disposition,
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

        ArcanumMaintenanceLockAcquisitionResult acquisition =
            ArcanumMaintenanceLock.AcquireDetailed(_paths.GrimoireDirectory);

        using ArcanumMaintenanceLock? maintenance = acquisition.Lock;

        BackupRestorePlan plan = await BuildPlanAsync(
            request,
            recoveryPassphrase,
            acquisition.Disposition,
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

        // The one question the plan deliberately leaves open, asked here on the mutating path only: a
        // destructive protected-state choice needs its own confirmation, separate from the one that
        // authorized displacing the installation (§10.19.10).
        if (BackupRestoreProtectedStatePolicy.EvaluateRequest(request, ReconcilesProtectedState(request))
            is { IsRefusal: true } unconfirmed)
        {

            return Rejected(operationId, plan, phases, [unconfirmed.Blocker!]);

        }

        InstallationMaintenanceCoordinationLease? coordinationLease = null;

        try
        {

            if (request.ConflictMode is BackupRestoreConflictMode.ReplaceInstallation
                && _maintenanceCoordination is not null)
            {

                if (maintenance is null)
                {

                    return Rejected(
                        operationId,
                        plan,
                        phases,
                        [
                            new BackupVerifyIssue(
                                ErrorCodes.Data.ControlPathUnavailable,
                                "Replacement restore requires the exact acquired maintenance lock before client coordination."),
                        ]);

                }

                InstallationMaintenanceCoordinationResult coordinated =
                    await _maintenanceCoordination
                        .AcquireReplacementRestoreAsync(
                            maintenance,
                            operationId,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (coordinated.Disposition
                    is not InstallationMaintenanceCoordinationDisposition.Acquired)
                {

                    return Rejected(
                        operationId,
                        plan,
                        phases,
                        [Issue(coordinated.Error)]);

                }

                coordinationLease = coordinated.BorrowAcquiredLease();

            }

            BackupRestoreResult restored = await ExecuteAsync(
                request,
                recoveryPassphrase,
                operationId,
                plan,
                phases,
                maintenance,
                cancellationToken).ConfigureAwait(false);

            if (coordinationLease is not null
                && restored.Status is not BackupRestoreStatus.ReconciliationRequired)
            {

                Result removed = await coordinationLease
                    .RemoveBlockerIfSafeAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (removed.IsFailure)
                {

                    restored = restored with
                    {
                        Status = BackupRestoreStatus.ReconciliationRequired,
                        Issues = [.. restored.Issues, Issue(removed.Error)],
                    };

                }

            }

            return restored;

        }
        finally
        {

            if (coordinationLease is not null)
            {

                await coordinationLease.DisposeAsync().ConfigureAwait(false);

            }

        }

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
        ArcanumMaintenanceLockAcquisitionDisposition maintenanceDisposition,
        CancellationToken cancellationToken)
    {

        DateTimeOffset generatedAt = _timeProvider.GetUtcNow();

        List<BackupVerifyIssue> blockers = [];

        List<string> warnings = [];

        string archivePath = string.IsNullOrWhiteSpace(request.ArchivePath)
            ? string.Empty
            : Path.GetFullPath(request.ArchivePath);

        string destinationRoot = ResolveDestinationRoot(request, blockers);

        bool maintenanceAcquired = maintenanceDisposition
            is ArcanumMaintenanceLockAcquisitionDisposition.Acquired;

        if (!maintenanceAcquired)
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_maintenance_unavailable",
                maintenanceDisposition is ArcanumMaintenanceLockAcquisitionDisposition.Contended
                    ? "Another process holds the Arcanum maintenance lock for this installation. Stop the "
                        + "running host, or wait for the installation reset or other restore to finish, then "
                        + "try again."
                    : "The Arcanum maintenance-lock topology, identity, or owner-only permissions "
                        + "could not be validated safely. Inspect the installation path and its "
                        + "permissions before trying again; the current installation is unchanged.",
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

        // Refused rather than ignored. Adoption happens inside the re-wrap, and only a replacement
        // rebuilds local secret protection at all — so anywhere else the option produced a plan
        // warning promising the archived key would replace this machine's, and then did nothing and
        // said nothing.
        if (request.RestoreMasterApiKey
            && request.ConflictMode != BackupRestoreConflictMode.ReplaceInstallation)
        {

            blockers.Add(new BackupVerifyIssue(
                "backup.restore_master_api_key_not_applicable",
                "Adopting the archived master API key applies only to the replace-installation "
                + "conflict mode, which is the only one that rebuilds local secret protection.",
                BackupArchivePaths.MasterApiKey));

        }

        BackupPathRemapValidation mapping = BackupPathRemapper.Create(
            request.PathMappings ?? []);

        blockers.AddRange(mapping.Issues);

        bool covenantImportArmActive = _options.SelectiveImport is not null;

        blockers.AddRange(
            BackupRestoreCampaignMappingPolicy.EvaluateShape(request, covenantImportArmActive));

        // Only a mapping that could still be honoured is worth a second read-only open of the live
        // Grimoire. A destination this machine cannot read contributes nothing rather than reporting
        // every mapped Campaign as absent: the import already refuses an unreadable destination with
        // backup.restore_import_destination_unavailable, and a second refusal about the mappings would
        // name the wrong cause (§10.19.12).
        //
        // The maintenance lock and the blockers so far both gate it, because this plan has already
        // decided the restore cannot proceed and the read is neither free nor harmless: it derives the
        // Grimoire key and opens the live database, which is exactly the touch the maintenance lock
        // exists to keep away from a running host.
        if (maintenanceAcquired
            && blockers.Count == 0
            && BackupRestoreCampaignMappingPolicy.RequiresDestinationCampaigns(
                request,
                covenantImportArmActive)
            && await ReadDestinationCampaignIdsAsync(cancellationToken).ConfigureAwait(false)
                is { } destinationCampaigns)
        {

            blockers.AddRange(
                BackupRestoreCampaignMappingPolicy.EvaluateDestination(
                    request,
                    covenantImportArmActive,
                    destinationCampaigns));

        }

        // Applicability only. The confirmation arm belongs to the mutating path, because this plan is
        // exactly what an operator surface reads in order to compose the confirmation it is about to ask
        // for (§10.19.10).
        if (BackupRestoreProtectedStatePolicy
                .EvaluateRequestShape(request, ReconcilesProtectedState(request))
            is { IsRefusal: true } shape)
        {

            blockers.Add(shape.Blocker!);

        }

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

        BackupRestoreDisclosureExposure? exposure =
            await ReadDestinationDisclosureAsync(request, cancellationToken).ConfigureAwait(false);

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
            [.. blockers],
            request.ProtectedStateMode,
            exposure);

    }

    /// <summary>
    /// Folds the destination's own nonrevocable disclosure receipts into the count a destructive
    /// protected-state choice has to be preceded by.
    /// </summary>
    /// <remarks>
    /// Read only for a restore that actually asks for a protected-state effect. It costs a second
    /// read-only open of the destination — and therefore a key derivation — so paying it on every plan
    /// would slow the default path for a number only a destructive choice is ever shown. A restore that
    /// does not enter the Covenant arm has no such choice to make and reports nothing.
    /// </remarks>
    private async Task<BackupRestoreDisclosureExposure?> ReadDestinationDisclosureAsync(
        BackupRestoreRequest request,
        CancellationToken cancellationToken)
    {

        if (request.ProtectedStateMode is BackupProtectedStateMode.Reject
            || !ReconcilesProtectedState(request))
        {

            return null;

        }

        BackupCovenantRestoreDestinationState destination =
            await ReadDestinationCovenantStateAsync(cancellationToken).ConfigureAwait(false);

        return BackupRestoreProtectedStateInspector.Exposure(destination.DisclosureBuckets);

    }

    private async Task<BackupRestoreResult> ExecuteAsync(
        BackupRestoreRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        Guid operationId,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        ArcanumMaintenanceLock? maintenance,
        CancellationToken cancellationToken)
    {

        string liveRoot = Path.GetFullPath(_paths.GrimoireDirectory);

        string stagingParent = Path.GetDirectoryName(
            request.ConflictMode == BackupRestoreConflictMode.NewProfileRoot
                ? plan.DestinationRoot
                : liveRoot)
            ?? throw new InvalidOperationException("The restore destination has no parent directory.");

        OwnedTemporaryDirectory staging;

        string stagedRoot;

        string displacedRoot;

        string workRoot;

        BackupRestoreJournalRecord journal;

        BackupSecretRewrapper rewrapper = new(_secretStore);

        BackupSecretSnapshot priorSecrets;

        // Everything the phase loop needs before it can run, under a refusal of its own. None of it
        // is covered by the try below, and an IO or permission failure here — an unwritable
        // new-profile parent, a quota the capacity plan could not model — used to leave RestoreAsync
        // by throwing, which strips the plan, the phase records, and the statement that the current
        // installation is untouched down to a generic CLI error.
        string? indexedStagingPath = null;

        OwnedTemporaryDirectory? createdStaging = null;

        try
        {

            _options.BeforeFirstRestoreMutationForTests?.Invoke();

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(stagingParent);

            string stagingPath = Path.Combine(stagingParent, BackupRestoreJournal.CreateStagingName());

            // Recorded before the root exists, and beside the live installation rather than inside
            // it: a new-profile restore stages beside its destination, where the startup sweep of the
            // live root's parent would never look for it.
            BackupRestoreStagingIndex.Add(liveRoot, stagingPath);

            indexedStagingPath = stagingPath;

            createdStaging = OwnedTemporaryDirectory.Create(stagingPath);

            staging = createdStaging;

            stagedRoot = Path.Combine(staging.Path, BackupRestoreJournal.StagedDirectoryName);

            displacedRoot = Path.Combine(staging.Path, BackupRestoreJournal.DisplacedDirectoryName);

            workRoot = Path.Combine(staging.Path, BackupRestoreJournal.WorkDirectoryName);

            journal = BackupRestoreJournal.Write(
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

            priorSecrets = await rewrapper.CaptureAsync().ConfigureAwait(false);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            if (createdStaging is not null)
            {

                // Journal first, exactly as the outer finally does. This catch is reachable with the
                // journal already written, and BackupRestoreJournal.Discover adopts a staging root only
                // while it still holds one — so a TryDelete that fails would otherwise leave the startup
                // sweep a Stage-phase journal to resume, for a restore that touched nothing.
                BackupRestoreJournal.Delete(createdStaging.Path);

                _ = createdStaging.TryDelete();

            }

            if (indexedStagingPath is not null)
            {

                BackupRestoreStagingIndex.Remove(liveRoot, indexedStagingPath);

            }

            return Rejected(
                operationId,
                plan,
                phases,
                [
                    new BackupVerifyIssue(
                        "backup.restore_failed",
                        "The restore could not prepare its staging root, so it stopped before any "
                        + "destructive step; the current installation is unchanged. Diagnostics: "
                        + exception.GetType().Name,
                        stagingParent),
                ]);

        }

        CommitOutcome? commit = null;

        // An unverifiable reversal leaves the displaced installation inside staging, so the cleanup
        // below must not run: the journal plus the staging root are the operator's only recovery
        // point until BackupRestoreRecovery resolves them at the next start.
        bool retainStagingForReconciliation = false;

        BackupRestorePlan effectivePlan = plan;

        string? safetyBackupPath = null;

        // The Covenant arm's own state. `durablyDisplaced` is filesystem evidence rather than a
        // success flag: it turns true the moment the two renames land and false again only when a
        // reversal is verified, and it is the single input that decides whether an abort may reopen
        // admission or must leave it closed for the next start.
        BackupRestoreCovenantSession? covenant = null;

        BackupRestoreCovenantTopology covenantTopology = new(
            liveRoot,
            stagedRoot,
            displacedRoot,
            plan.ArchivePath);

        bool durablyDisplaced = false;

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

            // Before the staged generation is composed, and before any owner is acquired. The archive
            // has to be readable to be inventoried at all, but a refusal here has closed no admission,
            // published no authenticated journal, and laid down no staged tree — and the cleanup below
            // removes the work directory it was read from (§10.19.10).
            BackupRestoreProtectedStateDecision protectedState = await EvaluateProtectedStateAsync(
                request,
                extractRoot,
                phases,
                cancellationToken).ConfigureAwait(false);

            if (protectedState.IsRefusal)
            {

                return Rejected(operationId, plan, phases, [protectedState.Blocker!]);

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

            BackupCovenantRestoreDestinationState destination =
                BackupCovenantRestoreDestinationState.None;

            if (ReconcilesProtectedState(request) && maintenance is not null)
            {

                destination = await ReadDestinationCovenantStateAsync(cancellationToken)
                    .ConfigureAwait(false);

                Result<BackupRestoreCovenantSession> begun = await _covenant!.BeginAsync(
                    maintenance,
                    liveRoot,
                    operationId,
                    request,
                    covenantTopology,
                    ManifestDigest(extraction.Manifest),
                    DestinationInstallationId(destination),
                    cancellationToken).ConfigureAwait(false);

                if (begun.IsFailure)
                {

                    return Rejected(operationId, plan, phases, [Issue(begun.Error)]);

                }

                covenant = begun.Value;

            }

            StageResult staged = await PrepareStagedGenerationAsync(
                request,
                extractRoot,
                stagedRoot,
                plan,
                phases,
                covenant,
                maintenance,
                liveRoot,
                covenantTopology,
                destination,
                protectedState.Outcome is BackupRestoreProtectedStateOutcome.PurgeStaging,
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

            // The authenticated journal reaches Commit before the first rename, so a process death
            // inside the two-rename window leaves evidence that names both roots by identity.
            if (covenant is not null
                && _covenant!.Advance(
                        covenant,
                        maintenance!,
                        liveRoot,
                        request,
                        BackupRestorePhase.Commit,
                        covenantTopology)
                    is { IsFailure: true } advanced)
            {

                return Rejected(operationId, effectivePlan, phases, [Issue(advanced.Error)]);

            }

            commit = Commit(
                request.ConflictMode,
                liveRoot,
                effectivePlan.DestinationRoot,
                stagedRoot,
                displacedRoot);

            durablyDisplaced = commit.Succeeded || commit.Reversal is { Restored: false };

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

                    durablyDisplaced = !reversal.Restored;

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

            // Everything below is the single admission reopen. The journal reaches its post-swap shape
            // first, then the marker children this restore committed into staging are proven in the
            // database they are now part of, and only a committed aggregate spends the disposition.
            if (covenant is not null)
            {

                Result published = _covenant!.Advance(
                    covenant,
                    maintenance!,
                    liveRoot,
                    request,
                    BackupRestorePhase.Reconcile,
                    covenantTopology);

                Result reopened = published.IsFailure
                    ? published
                    : await _covenant
                        .CompleteCommittedAsync(covenant, maintenance!, liveRoot, cancellationToken)
                        .ConfigureAwait(false);

                if (reopened.IsFailure)
                {

                    // The replacement is in place and healthy enough to have been reconciled, but
                    // admission stays shut and the journal stays active, so the next start resumes
                    // this same operation rather than restarting it.
                    retainStagingForReconciliation = true;

                    Record(
                        phases,
                        BackupRestorePhase.Reconcile,
                        "The restored generation is committed, but Covenant admission stays closed "
                        + "until an operator resolves this restore.");

                    return new BackupRestoreResult(
                        BackupRestoreStatus.ReconciliationRequired,
                        effectivePlan.ArchivePath,
                        operationId,
                        request.ConflictMode,
                        effectivePlan.DestinationRoot,
                        safetyBackupPath,
                        effectivePlan,
                        extraction.Manifest,
                        reconciliation,
                        [.. phases],
                        [Issue(reopened.Error)]);

                }

            }

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

                durablyDisplaced = !reversal.Restored;

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

                durablyDisplaced = !reversal.Restored;

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

            // One disposition per lease, and only where the filesystem proves nothing durable can have
            // happened. Everything else disposes without one, which is exactly KeepClosed: the closure
            // and the adoptable owner survive for the next start.
            if (covenant is { Dispositioned: false } && maintenance is not null)
            {

                _ = await _covenant!
                    .AbortAsync(
                        covenant,
                        maintenance,
                        liveRoot,
                        provenPreSwap: !durablyDisplaced,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            }

            if (!retainStagingForReconciliation)
            {

                BackupRestoreJournal.Delete(staging.Path);

                _ = staging.TryDelete();

                BackupRestoreStagingIndex.Remove(liveRoot, staging.Path);

            }

        }

    }

    /// <summary>
    /// Inventories the extracted archive's protected state and applies the requested mode to it.
    /// </summary>
    /// <remarks>
    /// Only a restore that runs the Covenant arm inventories anything. With the feature gate off there
    /// is no staged Covenant tier to preserve or remove and the default authorizes no effect, so the
    /// whole path collapses to <see cref="BackupRestoreProtectedStateInventory.None"/> and a restore is
    /// byte-for-byte what it was before this slice. A new-profile restore is outside the arm for the same
    /// reason it is outside §10.19.9: it displaces nothing, and the plan already warns that such a
    /// generation has to be adopted through a replace-installation restore — which is where the
    /// enforcement applies — before it is used.
    /// </remarks>
    private async Task<BackupRestoreProtectedStateDecision> EvaluateProtectedStateAsync(
        BackupRestoreRequest request,
        string extractRoot,
        List<BackupRestorePhaseRecord> phases,
        CancellationToken cancellationToken)
    {

        if (!ReconcilesProtectedState(request))
        {

            return BackupRestoreProtectedStatePolicy.EvaluateArchive(
                request.ProtectedStateMode,
                BackupRestoreProtectedStateInventory.None);

        }

        BackupRestoreProtectedStateInventory inventory = await BackupRestoreProtectedStateInspector
            .InspectExtractedArchiveAsync(extractRoot, cancellationToken)
            .ConfigureAwait(false);

        BackupRestoreProtectedStateDecision decision = BackupRestoreProtectedStatePolicy.EvaluateArchive(
            request.ProtectedStateMode,
            inventory);

        Record(
            phases,
            BackupRestorePhase.Stage,
            $"The archive carries {inventory.CanonicalRows} Covenant canonical rows, "
            + $"{inventory.AcceleratorRows} search projections, and {inventory.ProtectedArtifacts} "
            + "sensitivity labels; its authority state is "
            + (inventory.SourceAuthorityTainted ? "not provably clean" : "clean")
            + $". Protected-state mode: {request.ProtectedStateMode}.");

        return decision;

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
        BackupRestoreCovenantSession? covenant,
        ArcanumMaintenanceLock? maintenance,
        string liveRoot,
        BackupRestoreCovenantTopology covenantTopology,
        BackupCovenantRestoreDestinationState destination,
        bool purgeProtectedState,
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

        // Immediately after the three tiers converge and before any staged validation or destination
        // opener exists. Sanitation strips the archive's managed-file authority, the reconciliation
        // reissues this dataset's identities and joins the destination's evidence, and the marker
        // children commit in the same staged transaction — all against a candidate that has never
        // been published as live (§10.19.9).
        if (covenant is not null)
        {

            Result reconciled = await _covenant!.ReconcileStagedAsync(
                covenant,
                maintenance!,
                liveRoot,
                connection,
                destination,
                request,
                covenantTopology,
                purgeProtectedState,
                cancellationToken).ConfigureAwait(false);

            if (reconciled.IsFailure)
            {

                return StageResult.Failed(Issue(reconciled.Error));

            }

            Record(
                phases,
                BackupRestorePhase.Migrate,
                "Stripped the archive's managed-file authority, reissued this dataset's Covenant "
                + "identities, and committed this restore's Campaign marker children."
                + (purgeProtectedState
                    ? " The whole Covenant family and every protected artifact were removed from "
                        + "staging before replacement; the destination's own taint and disclosure "
                        + "evidence were preserved."
                    : string.Empty));

        }

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

        // With Covenant enabled the merge runs through the protected transfer store under one atomic
        // compound lease per Session; with it off, the plaintext path is byte-for-byte what it always
        // was. The gate decides, not the request, because a caller cannot be allowed to choose the
        // weaker of two import paths.
        BackupSessionImportResult import = _options.SelectiveImport is { } selectiveImport
            ? await BackupSessionImporter.ImportProtectedAsync(
                selectiveImport,
                Path.Combine(stagedRoot, "arcanum.db"),
                _paths.DatabasePath,
                plan.SelectedSessionIds,
                Path.Combine(stagedRoot, "attachments"),
                _paths.AttachmentsDirectory,
                destinationSecret.Value,
                ReadStagedSecret(extractRoot),
                request.CampaignMappings ?? [],
                cancellationToken).ConfigureAwait(false)
            : await BackupSessionImporter.ImportAsync(
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

            // Rejected means the destination was never mutated, so it is only available while nothing
            // committed. A protected import commits Session by Session, and once one has landed the
            // truthful outcome is a committed installation that still needs an operator.
            return import.Committed.Length == 0
                ? Rejected(operationId, plan, phases, import.Issues)
                : PartiallyImported(operationId, request, plan, phases, import);

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

    /// <summary>
    /// The outcome when a selective import was refused after earlier Sessions had already committed.
    /// </summary>
    /// <remarks>
    /// Everything here exists because the alternative was <see cref="BackupRestoreStatus.Rejected"/>,
    /// whose whole meaning is that the destination was not mutated — reported over an installation
    /// that had gained Sessions, with a null reconciliation that carried no count at all.
    ///
    /// <para>The committed Sessions are named by both identities. A protected import mints the
    /// destination identity per run, so nothing the operator already has can be matched to the
    /// selection they asked for without being told the pairing, and nothing about a re-run is safe:
    /// the store's replay guard is keyed to an operation identity this restore will never present
    /// again, so the same selection imports the same Sessions a second time under new identities. The
    /// issue says so rather than leaving the operator to discover it.</para>
    /// </remarks>
    private static BackupRestoreResult PartiallyImported(
        Guid operationId,
        BackupRestoreRequest request,
        BackupRestorePlan plan,
        List<BackupRestorePhaseRecord> phases,
        BackupSessionImportResult import)
    {

        string pairs = string.Join(
            ", ",
            import.Committed.Select(
                static committed =>
                    $"{committed.SourceSessionId:D} as {committed.DestinationSessionId:D}"));

        Record(
            phases,
            BackupRestorePhase.Commit,
            $"Imported {import.Sessions} Sessions, {import.Entries} entries, and "
            + $"{import.Attachments} attachments before the import was refused; the destination holds "
            + pairs + ".");

        Record(
            phases,
            BackupRestorePhase.Reconcile,
            "The import stopped partway, so this installation needs an operator: it holds the "
            + "Sessions named above and none of the ones after them.");

        return new BackupRestoreResult(
            BackupRestoreStatus.ReconciliationRequired,
            plan.ArchivePath,
            operationId,
            request.ConflictMode,
            plan.DestinationRoot,
            SafetyBackupPath: null,
            plan,
            Manifest: null,
            new BackupRestoreReconciliation(
                import.Attachments,
                import.Attachments,
                UploadedFiles: 0,
                BatchFiles: 0,
                EmbeddingsRebuilt: 0,
                PendingOperationsCleared: 0,
                Issues: []),
            [.. phases],
            [
                .. import.Issues,
                new BackupVerifyIssue(
                    "backup.restore_import_partially_committed",
                    $"{import.Sessions} Sessions were already imported into this installation before "
                    + $"the refusal above: {pairs}. Do not re-run this import as it stands — every run "
                    + "mints new identities, so the same selection would import them a second time. "
                    + "Remove the imported Sessions, or re-run naming only the Sessions that did not "
                    + "land."),
            ]);

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

    /// <summary>
    /// The Campaign identities this installation holds, or <see langword="null"/> when it cannot say.
    /// </summary>
    /// <remarks>
    /// Three-valued on purpose. An empty set is the true statement "this machine has no Campaigns",
    /// which refuses every mapping; <see langword="null"/> is "this machine could not be asked", which
    /// must refuse none of them. Collapsing the two would turn a missing credential or an unopenable
    /// database into a typed complaint about the operator's Campaign mapping — the one thing that is
    /// certainly not wrong in that situation.
    /// </remarks>
    private async Task<IReadOnlyCollection<Guid>?> ReadDestinationCampaignIdsAsync(
        CancellationToken cancellationToken)
    {

        if (!File.Exists(_paths.DatabasePath))
        {

            return null;

        }

        try
        {

            SecretStoreReadResult secret = await _secretStore
                .GetGrimoireEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            if (secret.Status != SecretStoreReadStatus.Ok || string.IsNullOrEmpty(secret.Value))
            {

                return null;

            }

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                .OpenAsync(_paths.DatabasePath, secret.Value, readOnly: true, cancellationToken)
                .ConfigureAwait(false);

            return await BackupRestoreDatabaseWorker
                .ReadCampaignIdsAsync(connection, cancellationToken)
                .ConfigureAwait(false);

        }
        // Wider than the sibling readers above, because opening the destination starts at the KDF
        // sidecar: an unsupported version, malformed JSON, or an unusable salt are all "this machine
        // could not be asked", and letting one escape would abort a rehearsal that the same command
        // without --map-campaign completes — which reads as the mapping option having caused it.
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or FormatException
                or System.Text.Json.JsonException
                or System.Security.Cryptography.CryptographicException)
        {

            return null;

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

    /// <summary>
    /// Reads the destination's authority and disclosure evidence before anything is displaced.
    /// </summary>
    /// <remarks>
    /// Values, not a retained handle. The staged reconciliation runs inside its own transaction on a
    /// different database, and a live connection held open across it would be a second writer to an
    /// installation this operation has already closed admission on. A destination that cannot be
    /// opened contributes nothing, which the monotonic join treats as a clean lineage at epoch zero —
    /// the archive's own taint and epoch both survive that.
    /// </remarks>
    private async Task<BackupCovenantRestoreDestinationState> ReadDestinationCovenantStateAsync(
        CancellationToken cancellationToken)
    {

        if (!File.Exists(_paths.DatabasePath))
        {

            return BackupCovenantRestoreDestinationState.None;

        }

        try
        {

            SecretStoreReadResult secret = await _secretStore
                .GetGrimoireEncryptionSecretReadResultAsync()
                .ConfigureAwait(false);

            if (secret.Status != SecretStoreReadStatus.Ok || string.IsNullOrEmpty(secret.Value))
            {

                return BackupCovenantRestoreDestinationState.None;

            }

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                .OpenAsync(_paths.DatabasePath, secret.Value, readOnly: true, cancellationToken)
                .ConfigureAwait(false);

            return await BackupCovenantRestoreDestinationState
                .ReadAsync(connection, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
        {

            return BackupCovenantRestoreDestinationState.None;

        }

    }

    /// <summary>
    /// The installation this restore's journal binds to, or empty when the destination names none.
    /// </summary>
    private static Guid DestinationInstallationId(BackupCovenantRestoreDestinationState destination) =>
        destination.Authority is { } authority
        && Guid.TryParse(authority.InstallationIdentity, out Guid installationId)
            ? installationId
            : Guid.Empty;

    /// <summary>
    /// A one-way commitment to the authenticated archive manifest this restore is reading.
    /// </summary>
    /// <remarks>
    /// Over the source-generated encoding of the manifest the codec already authenticated, so the
    /// digest is a property of the archive rather than of how this build happened to read it.
    /// </remarks>
    private static CovenantDigest ManifestDigest(BackupManifest manifest) =>
        new(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    manifest,
                    BackupJsonContext.Default.BackupManifest)));

    /// <summary>
    /// Reports a Covenant refusal in the vocabulary the restore result already speaks.
    /// </summary>
    /// <remarks>
    /// The typed code is carried through rather than flattened to one restore code, because the
    /// operator's next step differs sharply between "this archive cannot be adopted here" and "this
    /// installation is already inside another exclusive operation".
    /// </remarks>
    private static BackupVerifyIssue Issue(Error error) =>
        new(error.Code, error.Message);

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
