using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal enum BackupRestoreRecoveryOutcome
{

    /// <summary>Staging was found before any destructive step and was simply removed.</summary>
    Discarded = 0,

    /// <summary>An interrupted commit was reversed; the prior installation is back in place.</summary>
    RolledBack = 1,

    /// <summary>The commit had completed; only staging cleanup remained.</summary>
    CommitCompleted = 2,

    /// <summary>The commit landed but post-commit work is unverifiable; an operator must decide.</summary>
    ReconciliationRequired = 3,

}

internal sealed record BackupRestoreRecoveryReport(
    string StagingRoot,
    BackupRestoreRecoveryOutcome Outcome,
    BackupRestorePhase Phase,
    string Detail);

/// <summary>
/// Resolves restore staging left behind by a process that died mid-restore.
/// </summary>
/// <remarks>
/// Two generations of the same job live here. <see cref="Resolve"/> is the pre-Covenant path of
/// §5.4.9: the plain journal plus the filesystem's own evidence are jointly authoritative, and every
/// combination maps to exactly one action — discard, reverse, finish, or stop and ask. It still runs,
/// because <c>BackupRestoreService</c> still writes that journal, but only once the authenticated
/// stack below has proved that no V2 evidence exists.
/// <para>
/// The two <see cref="IBackupRestoreStartupRecovery"/> methods are the authenticated replacement. They
/// believe an anchored AES-GCM envelope rather than a document anyone with write access to the staging
/// directory could mint, they converge topology from durable node identities rather than from
/// presence, and they resume the restore's exclusive owner instead of guessing from the tree what it
/// was allowed to do (§10.19.8).
/// </para>
/// <para>
/// Which roots get looked at is a separate question from what to do with them. Staging normally sits
/// beside the live root and is found by sweeping its parent; a new-profile restore has to stage
/// beside its destination instead, and <see cref="BackupRestoreStagingIndex"/> is what keeps those
/// roots reachable from here.
/// </para>
/// </remarks>
internal sealed class BackupRestoreRecovery : IBackupRestoreStartupRecovery
{

    private readonly string _guardedDirectory;

    private readonly BackupRestoreJournalAnchorStore _anchors;

    private readonly CovenantOperationGate? _gate;

    private readonly ICampaignPathMarkerLifecycle? _markers;

    /// <summary>
    /// Binds one recovery to the installation it may recover and to the collaborators it borrows.
    /// </summary>
    /// <remarks>
    /// The gate and the marker lifecycle are optional because the first phase needs neither and runs in
    /// bootstraps that compose neither. Their absence is never treated as permission: authority
    /// recovery without them keeps admission closed rather than concluding there was nothing to resume.
    /// </remarks>
    internal BackupRestoreRecovery(
        string guardedDirectory,
        BackupRestoreJournalAnchorStore anchors,
        CovenantOperationGate? gate,
        ICampaignPathMarkerLifecycle? markers)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        ArgumentNullException.ThrowIfNull(anchors);

        _guardedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(guardedDirectory));

        _anchors = anchors;

        _gate = gate;

        _markers = markers;

    }

    /// <inheritdoc />
    public Task<Result<BackupRestorePhysicalRecoveryOutcome>> RecoverPhysicalTopologyBeforeDatabaseAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        cancellationToken.ThrowIfCancellationRequested();

        // Identity, not liveness, and deliberately no acquisition: the caller holds this lock for the
        // whole of startup and it is borrowed here rather than taken again.
        heldInstallationLock.AssertHeldFor(_guardedDirectory);

        Result<BackupRestoreEvidence?> evidence = Authenticate(heldInstallationLock);

        if (evidence.IsFailure)
        {

            return Task.FromResult(KeptPhysical(evidence.Error));

        }

        if (evidence.Value is not { } active)
        {

            return Task.FromResult(
                Result<BackupRestorePhysicalRecoveryOutcome>.Success(
                    BackupRestorePhysicalRecoveryOutcome.NoActiveJournal));

        }

        Result<BackupRestoreTopologyState> converged = BackupRestorePhysicalTopology.Reconcile(
            active.Publication.Payload,
            _guardedDirectory,
            allowEffects: true);

        // The journal and its active anchor are left exactly as they were. Convergence proves which
        // tree is live; it proves nothing about whether that tree may serve.
        return Task.FromResult(
            converged.IsFailure
                ? KeptPhysical(converged.Error)
                : Result<BackupRestorePhysicalRecoveryOutcome>.Success(
                    BackupRestorePhysicalRecoveryOutcome.TopologyReady));

    }

    /// <inheritdoc />
    public async Task<Result<BackupRestoreStartupRecoveryOutcome>> RecoverAuthorityBeforeReadinessAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        cancellationToken.ThrowIfCancellationRequested();

        heldInstallationLock.AssertHeldFor(_guardedDirectory);

        Result<BackupRestoreEvidence?> evidence = Authenticate(heldInstallationLock);

        if (evidence.IsFailure)
        {

            return Kept(evidence.Error);

        }

        if (evidence.Value is not { } active)
        {

            return BackupRestoreStartupRecoveryOutcome.NoActiveJournal;

        }

        Result<CovenantExclusiveRecoveryOwner> owner = active.Publication.Payload.RecoveryOwner();

        if (owner.IsFailure)
        {

            return Kept(owner.Error);

        }

        if (_gate is null || _markers is null)
        {

            return Kept(new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "An interrupted restore cannot be resumed by a process that composed no Covenant "
                + "operation gate."));

        }

        // Re-derived rather than remembered. The first phase converged the tree and then a database was
        // opened against it; what may be resumed now is a property of the tree as it stands.
        Result<BackupRestoreTopologyState> topology = BackupRestorePhysicalTopology.Reconcile(
            active.Publication.Payload,
            _guardedDirectory,
            allowEffects: false);

        if (topology.IsFailure)
        {

            return Kept(topology.Error);

        }

        try
        {

            _gate.AdoptDurableRecoveryOwner(
                owner.Value,
                scope: null,
                cleanupOnlyHistoricalCampaign: false);

        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {

            return Kept(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore's exclusive owner could not be reconstructed before readiness."));

        }

        Result<CovenantExclusiveLease> resumed = await _gate
            .ResumeExclusiveAsync(owner.Value, cancellationToken)
            .ConfigureAwait(false);

        if (resumed.IsFailure)
        {

            return Kept(resumed.Error);

        }

        // Disposal without a disposition is exactly KeepClosed: the closure survives, the owner stays
        // adoptable, and the next start resumes the same operation rather than restarting it.
        await using CovenantExclusiveLease lease = resumed.Value;

        Result<BackupRestoreStartupRecoveryOutcome> outcome =
            topology.Value is BackupRestoreTopologyState.PreSwap
                ? await RollBackAsync(heldInstallationLock, active, owner.Value, lease, cancellationToken)
                    .ConfigureAwait(false)
                : await CommitAsync(heldInstallationLock, active, owner.Value, lease, cancellationToken)
                    .ConfigureAwait(false);

        if (outcome.IsSuccess && outcome.Value is BackupRestoreStartupRecoveryOutcome.KeptClosed)
        {

            // The restart proof of §10.19.7 may have reopened roots for children this pass could not
            // finish. Nothing else in this process will adopt them and startup is about to stop, so the
            // one release that covers both arms runs here too.
            await _markers.ReleaseRetainedRootsAsync(owner.Value.OperationId).ConfigureAwait(false);

        }

        return outcome;

    }

    /// <summary>
    /// Reverses a restore that never displaced the installation.
    /// </summary>
    /// <remarks>
    /// <c>RollbackAndReopen</c> is the only honest disposition here, and it is available precisely
    /// because physical convergence proved that the live root is still the tree that was always there.
    /// No marker child can exist to reconcile: preparation commits into the staged database, which this
    /// arm is about to discard.
    /// </remarks>
    private async Task<Result<BackupRestoreStartupRecoveryOutcome>> RollBackAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        BackupRestoreEvidence active,
        CovenantExclusiveRecoveryOwner owner,
        CovenantExclusiveLease lease,
        CancellationToken cancellationToken)
    {

        Result reopened = await lease
            .CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                CovenantNoOpPostDispositionFinalizer.Instance,
                cancellationToken)
            .ConfigureAwait(false);

        if (reopened.IsFailure)
        {

            return Kept(reopened.Error);

        }

        return await TerminateAsync(heldInstallationLock, active, owner, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Carries a restore that already replaced the installation to its one commit.
    /// </summary>
    /// <remarks>
    /// The authenticated checkpoint is the only list of children this may touch, and the lifecycle
    /// returns the disposition rather than applying it. Anything short of a commit — a child that
    /// cannot be proved, an aggregate outcome that is not committed, a disposition that fails — leaves
    /// the journal active and admission shut, because the alternative is reopening on the strength of
    /// evidence nobody produced.
    /// </remarks>
    private async Task<Result<BackupRestoreStartupRecoveryOutcome>> CommitAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        BackupRestoreEvidence active,
        CovenantExclusiveRecoveryOwner owner,
        CovenantExclusiveLease lease,
        CancellationToken cancellationToken)
    {

        if (active.Publication.Payload.MarkerCleanup is not { } checkpoint)
        {

            return Kept(new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A restore that displaced the installation carries no marker cleanup checkpoint."));

        }

        Result<CampaignPathMarkerGateCompletion> completion = await _markers!
            .ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    owner,
                    checkpoint.OrderedIntentIds,
                    checkpoint.IntentVectorDigest),
                lease,
                cancellationToken)
            .ConfigureAwait(false);

        if (completion.IsFailure)
        {

            return Kept(completion.Error);

        }

        if (completion.Value.Outcome is not CampaignPathMarkerAggregateOutcome.Committed
            || completion.Value.Disposition is not CovenantExclusiveLeaseDisposition.CommitAndReopen)
        {

            return Kept(new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "An interrupted restore's marker children did not reach a committed disposition."));

        }

        Result committed = await lease
            .CompleteAsync(completion.Value.Disposition, completion.Value.Finalizer, cancellationToken)
            .ConfigureAwait(false);

        if (committed.IsFailure)
        {

            return Kept(committed.Error);

        }

        return await TerminateAsync(heldInstallationLock, active, owner, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Releases the roots this owner held, tombstones the anchor, and only then removes staging.
    /// </summary>
    /// <remarks>
    /// The order is the anchor's, not this method's: the tombstone is written before the journal is
    /// deleted, so an active anchor can never name a file that no longer exists. Staging goes last,
    /// because until the anchor is closed the journal inside it is the evidence recovery runs on.
    /// </remarks>
    private async Task<Result<BackupRestoreStartupRecoveryOutcome>> TerminateAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        BackupRestoreEvidence active,
        CovenantExclusiveRecoveryOwner owner,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        await _markers!.ReleaseRetainedRootsAsync(owner.OperationId).ConfigureAwait(false);

        Result closed = _anchors.Close(
            heldInstallationLock,
            _guardedDirectory,
            active.Profile,
            active.Publication);

        if (closed.IsFailure)
        {

            return Kept(closed.Error);

        }

        DiscardStaging(active.Publication.Payload);

        return BackupRestoreStartupRecoveryOutcome.RecoveredReady;

    }

    /// <summary>
    /// Removes the staging root this restore owned, by the identity its journal committed to.
    /// </summary>
    /// <remarks>
    /// Best effort by design. The anchor is already a closed tombstone at this point, so a staging
    /// directory that survives is garbage rather than authority, and refusing readiness over it would
    /// wedge an installation whose restore has provably finished.
    /// </remarks>
    private void DiscardStaging(BackupRestoreJournalPayloadV2 payload)
    {

        string stagingRoot = payload.StagedRoot.CanonicalParentPath;

        if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                stagingRoot,
                out FileHandleMetadata metadata)
            && metadata.Kind is FileSystemObjectKind.Directory
            && BackupRestoreJournalAuthenticator.PhysicalIdentity(
                metadata.Identity.VolumeId,
                metadata.Identity.FileId)
                == payload.StagedRoot.ParentPhysicalIdentityDigest)
        {

            _ = OwnedTemporaryDirectory.TryDelete(
                stagingRoot,
                metadata.Identity.VolumeId,
                metadata.Identity.FileId);

        }

        PruneStagingIndex(_guardedDirectory);

    }

    /// <summary>
    /// Authenticates whatever restore evidence this profile carries, or proves that it carries none.
    /// </summary>
    private Result<BackupRestoreEvidence?> Authenticate(ArcanumMaintenanceLock heldInstallationLock)
    {

        string? parent = TryParentOf(_guardedDirectory);

        // No parent directory means no staging beside it, no profile namespace to derive, and no
        // installation to have restored. That is proven absence rather than an unreadable answer.
        if (parent is null || !Directory.Exists(parent))
        {

            return Result<BackupRestoreEvidence?>.Success(null);

        }

        Result<BackupRestoreProfileNamespace> profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(_guardedDirectory);

        if (profile.IsFailure)
        {

            return Result<BackupRestoreEvidence?>.Failure(profile.Error);

        }

        Result<BackupRestoreJournalRecoveryState> state = _anchors.Recover(
            heldInstallationLock,
            _guardedDirectory,
            profile.Value,
            CandidateStagingRoots(parent));

        if (state.IsFailure)
        {

            return Result<BackupRestoreEvidence?>.Failure(state.Error);

        }

        return state.Value.Outcome is BackupRestoreJournalRecoveryOutcome.NoActiveJournal
            || state.Value.Publication is not { } publication
                ? Result<BackupRestoreEvidence?>.Success(null)
                : new BackupRestoreEvidence(profile.Value, publication);

    }

    /// <summary>
    /// Every directory a V2 journal could be sitting in: the canonical sweep plus the staging index.
    /// </summary>
    /// <remarks>
    /// Canonically named roots are included whether or not they hold a journal, because proving absence
    /// is the point: a lookalike nothing commits to has to be visible to the authenticator, which is
    /// the only thing entitled to decide that it is not evidence.
    /// </remarks>
    private IReadOnlyList<string> CandidateStagingRoots(string parent)
    {

        List<string> candidates = [];

        try
        {

            foreach (string candidate in Directory.EnumerateDirectories(
                         parent,
                         BackupRestoreJournal.StagingPrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {

                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

                if (BackupRestoreJournal.IsCanonicalStagingName(Path.GetFileName(full)))
                {

                    candidates.Add(full);

                }

            }

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

        foreach (string indexed in BackupRestoreStagingIndex.Read(_guardedDirectory))
        {

            if (TryNormalize(indexed) is { } normalized && !candidates.Contains(normalized, StringComparer.Ordinal))
            {

                candidates.Add(normalized);

            }

        }

        return candidates;

    }

    private static string? TryParentOf(string directory)
    {

        try
        {

            return Path.GetDirectoryName(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return null;

        }

    }

    private static Result<BackupRestorePhysicalRecoveryOutcome> KeptPhysical(Error error)
    {

        Warn(error);

        return Result<BackupRestorePhysicalRecoveryOutcome>.Success(
            BackupRestorePhysicalRecoveryOutcome.KeptClosed);

    }

    private static Result<BackupRestoreStartupRecoveryOutcome> Kept(Error error)
    {

        Warn(error);

        return Result<BackupRestoreStartupRecoveryOutcome>.Success(
            BackupRestoreStartupRecoveryOutcome.KeptClosed);

    }

    private static void Warn(Error error) =>
        Log.Warning(
            "An interrupted Arcanum restore could not be resolved, so startup stays closed: {Code} {Message}",
            error.Code,
            error.Message);

    /// <summary>One profile's authenticated restore evidence, as recovery consumes it.</summary>
    private sealed record BackupRestoreEvidence(
        BackupRestoreProfileNamespace Profile,
        BackupRestoreJournalPublication Publication);

    public static IReadOnlyList<BackupRestoreRecoveryReport> Resolve(string grimoireDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(grimoireDirectory);

        string liveRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(grimoireDirectory));

        string? parent = Path.GetDirectoryName(liveRoot);

        if (parent is null)
        {

            return [];

        }

        IReadOnlyList<string> indexed = BackupRestoreStagingIndex.Read(grimoireDirectory);

        List<string> indexedRoots = [.. indexed.Select(TryNormalize).OfType<string>()];

        List<string> stagingRoots = [.. BackupRestoreJournal.Discover(parent)];

        HashSet<string> seen = new(stagingRoots, StringComparer.Ordinal);

        stagingRoots.AddRange(indexedRoots.Where(seen.Add));

        List<BackupRestoreRecoveryReport> reports = [];

        foreach (string stagingRoot in stagingRoots)
        {

            BackupRestoreJournalRecord? journal = BackupRestoreJournal.TryRead(stagingRoot);

            if (journal is null)
            {

                continue;

            }

            reports.Add(ResolveOne(stagingRoot, journal, liveRoot));

        }

        if (indexedRoots.Count > 0)
        {

            // Rewriting the whole set rather than removing entry by entry is what prunes an index
            // still naming staging that was never created, or that some other sweep already took.
            PruneIndex(grimoireDirectory, indexedRoots);

        }

        return reports;

    }

    /// <summary>
    /// Rewrites the staging index with only the entries that still name a live staging root.
    /// </summary>
    /// <remarks>
    /// Rewriting the whole set rather than removing one entry is what prunes an index still naming
    /// staging that was never created, or that some other sweep already took. Unlike the pre-Covenant
    /// prune below it does not require a journal to be readable, because the authenticated path deletes
    /// the journal before the directory that held it.
    /// </remarks>
    private static void PruneStagingIndex(string grimoireDirectory)
    {

        try
        {

            BackupRestoreStagingIndex.Write(
                grimoireDirectory,
                [.. BackupRestoreStagingIndex.Read(grimoireDirectory)
                    .Select(TryNormalize)
                    .OfType<string>()
                    .Where(Directory.Exists)]);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    private static void PruneIndex(string grimoireDirectory, List<string> indexedRoots)
    {

        try
        {

            BackupRestoreStagingIndex.Write(
                grimoireDirectory,
                [.. indexedRoots.Where(BackupRestoreJournal.Exists)]);

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

        }

    }

    /// <summary>
    /// A staging root named by the index, or <see langword="null"/> when the entry is unusable or is
    /// not one of ours. The canonical-name check is the same guard <see cref="BackupRestoreJournal"/>
    /// applies when sweeping a directory: a hand-written entry must never make recovery delete an
    /// arbitrary path.
    /// </summary>
    private static string? TryNormalize(string stagingRoot)
    {

        try
        {

            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));

            return BackupRestoreJournal.IsCanonicalStagingName(Path.GetFileName(full))
                ? full
                : null;

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return null;

        }

    }

    private static BackupRestoreRecoveryReport ResolveOne(
        string stagingRoot,
        BackupRestoreJournalRecord journal,
        string liveRoot)
    {

        Result validation = BackupRestoreJournal.ValidateForRecovery(
            stagingRoot,
            liveRoot,
            journal);

        if (validation.IsFailure)
        {

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.ReconciliationRequired,
                journal.Phase,
                "The legacy restore journal could not be admitted safely and was left untouched.");

        }

        bool stagedExists = Directory.Exists(journal.StagedRoot);

        bool displacedExists = Directory.Exists(journal.DisplacedRoot);

        bool liveExists = Directory.Exists(journal.LiveRoot);

        if (journal.Phase < BackupRestorePhase.Commit)
        {

            Discard(stagingRoot, journal);

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.Discarded,
                journal.Phase,
                "The restore was interrupted before any destructive step; staging was removed and the "
                + "installation was never modified.");

        }

        if (journal.Phase > BackupRestorePhase.Commit)
        {

            Discard(stagingRoot, journal);

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.CommitCompleted,
                journal.Phase,
                "The restore had already committed; only staging cleanup remained.");

        }

        if (stagedExists && liveExists && !displacedExists)
        {

            Discard(stagingRoot, journal);

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.RolledBack,
                journal.Phase,
                "The commit had not begun; the prior installation was already in place.");

        }

        if (!liveExists && displacedExists)
        {

            try
            {

                Directory.Move(journal.DisplacedRoot, journal.LiveRoot);

                Discard(stagingRoot, journal);

                return new BackupRestoreRecoveryReport(
                    stagingRoot,
                    BackupRestoreRecoveryOutcome.RolledBack,
                    journal.Phase,
                    "The commit was interrupted between renames; the prior installation was restored.");

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                return new BackupRestoreRecoveryReport(
                    stagingRoot,
                    BackupRestoreRecoveryOutcome.ReconciliationRequired,
                    journal.Phase,
                    "The prior installation could not be moved back into place. It is preserved at "
                    + journal.DisplacedRoot);

            }

        }

        if (!stagedExists && liveExists && displacedExists)
        {

            return new BackupRestoreRecoveryReport(
                stagingRoot,
                BackupRestoreRecoveryOutcome.ReconciliationRequired,
                journal.Phase,
                "The restored generation is committed but its local secret protection may not have "
                + "been rebuilt. Re-run the restore, or verify the Grimoire opens. The prior "
                + "installation is preserved at " + journal.DisplacedRoot);

        }

        Discard(stagingRoot, journal);

        return new BackupRestoreRecoveryReport(
            stagingRoot,
            BackupRestoreRecoveryOutcome.CommitCompleted,
            journal.Phase,
            "The commit had completed; only staging cleanup remained.");

    }

    private static void Discard(string stagingRoot, BackupRestoreJournalRecord journal)
    {

        BackupRestoreJournal.Delete(stagingRoot);

        _ = OwnedTemporaryDirectory.TryDelete(
            stagingRoot,
            journal.StagingVolumeId,
            journal.StagingFileId);

    }

}
