using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The capabilities a full restore reconciles protected state through, present only with the gate on.
/// </summary>
/// <remarks>
/// Grouped and optional for the same reason the selective-import group is: with
/// <c>Arcanum:Features:Covenant</c> off there is no gate to close admission on, no marker protocol to
/// clean up after, and no authenticated journal to publish — and a restore that demanded them would
/// refuse on installations that have never enabled the feature.
/// </remarks>
internal sealed record CovenantRestoreStagingServices(
    ICovenantOperationGate Gate,
    ICampaignPathMarkerLifecycle Markers,
    BackupRestoreJournalAnchorStore Anchors,
    BackupRestoreJournalInstallationIdentityProvider Identities,
    BackupRestoreJournalKeyProvider Keys,
    IBackupRestoreEffectDigestCalculator EffectDigests);

/// <summary>
/// The filesystem nodes one restore's authenticated journal commits to.
/// </summary>
internal sealed record BackupRestoreCovenantTopology(
    string LiveRoot,
    string StagedRoot,
    string DisplacedRoot,
    string ArchivePath);

/// <summary>
/// One restore's live Covenant authority: its owner, its lease, and its published journal revision.
/// </summary>
/// <remarks>
/// Mutable in exactly two fields, both of which advance monotonically: the publication as each
/// revision lands, and the marker checkpoint once preparation has committed. Everything else is fixed
/// when the session opens, because the owner is what a restart compares against before it may adopt
/// this operation's closure.
/// </remarks>
internal sealed class BackupRestoreCovenantSession
{

    internal BackupRestoreCovenantSession(
        CovenantExclusiveRecoveryOwner owner,
        CovenantExclusiveLease lease,
        BackupRestoreProfileNamespace profile,
        Guid installationId,
        BackupRestoreJournalPublication publication,
        BackupRestoreJournalPayloadV2 payload)
    {

        Owner = owner;

        Lease = lease;

        Profile = profile;

        InstallationId = installationId;

        Publication = publication;

        Payload = payload;

    }

    internal CovenantExclusiveRecoveryOwner Owner { get; }

    internal CovenantExclusiveLease Lease { get; }

    internal BackupRestoreProfileNamespace Profile { get; }

    internal Guid InstallationId { get; }

    internal BackupRestoreJournalPublication Publication { get; private set; }

    internal BackupRestoreJournalPayloadV2 Payload { get; private set; }

    internal BackupRestoreMarkerCleanupCheckpointV1? Checkpoint { get; private set; }

    /// <summary>Whether the lease has already spent its one reopening decision.</summary>
    internal bool Dispositioned { get; private set; }

    internal void Publish(BackupRestoreJournalPublication publication, BackupRestoreJournalPayloadV2 payload)
    {

        Publication = publication;

        Payload = payload;

        Checkpoint = payload.MarkerCleanup ?? Checkpoint;

    }

    internal void MarkDispositioned() => Dispositioned = true;

}

/// <summary>
/// The Covenant arm of one full restore, from gate acquisition to the single admission reopen.
/// </summary>
/// <remarks>
/// It exists as its own type because the restore service already owns a complete filesystem protocol
/// and this is a complete authority protocol beside it. The two meet at four points and nowhere else:
/// the owner is acquired and journaled before any staged effect, the staged generation is reconciled
/// and its marker children committed before the first displacement, the children are reconciled after
/// the swap, and exactly one disposition is spent.
///
/// <para>Every failure below the disposition selects <c>KeepClosed</c> by disposing the lease without
/// one, which preserves the closure and the adoptable owner so the next start resumes this operation
/// rather than restarting it. Only a restore that provably changed nothing durable may select
/// <c>RollbackAndReopen</c> (§10.19.9).</para>
/// </remarks>
internal sealed class BackupRestoreCovenantCoordinator
{

    /// <summary>
    /// The domain the restore-request digest is computed under.
    /// </summary>
    /// <remarks>
    /// This commits to what the operator asked for, not to which exact destructive plan it authorizes;
    /// the effect digest is the latter and binds the physical archive, destination, and profile as
    /// well. Keeping the two apart is what lets the journal record the request that produced an owner
    /// without the request becoming a second, weaker way to reconstruct one.
    /// </remarks>
    private const string RequestDigestDomain = "Arcanum.BackupRestore.Request.v1";

    private readonly CovenantRestoreStagingServices _services;

    private readonly CovenantSqliteConnectionInitializer _initializer;

    private readonly TimeProvider _timeProvider;

    internal BackupRestoreCovenantCoordinator(
        CovenantRestoreStagingServices services,
        CovenantSqliteConnectionInitializer initializer,
        TimeProvider timeProvider)
    {

        _services = services ?? throw new ArgumentNullException(nameof(services));

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    }

    /// <summary>
    /// Closes admission under one exact owner and publishes the journal that names it.
    /// </summary>
    /// <remarks>
    /// The order is the whole point of this method. Nothing staged, nothing displaced, and nothing
    /// deleted has happened yet, so a crash anywhere after this leaves an authenticated journal whose
    /// owner startup recovery can resume — and a crash before it leaves an installation that was never
    /// touched.
    /// </remarks>
    internal async Task<Result<BackupRestoreCovenantSession>> BeginAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        Guid operationId,
        BackupRestoreRequest request,
        BackupRestoreCovenantTopology topology,
        CovenantDigest archiveManifestDigest,
        Guid destinationInstallationId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(topology);

        heldInstallationLock.AssertHeldFor(guardedDirectory);

        Result<BackupRestoreProfileNamespace> profile =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedDirectory);

        if (profile.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(profile.Error);

        }

        // The one place a journal key may be generated: a caller-held lock, before any restore work.
        // Recovery never creates one, because minting a replacement would strand every envelope
        // written under the previous key while presenting as a healthy first run (§10.19.6). The lease
        // is disposed unspent, which zeroizes the decoded bytes.
        Result<BackupRestoreJournalKeyLease> key = _services.Keys.CreateOrOpen(
            heldInstallationLock,
            guardedDirectory,
            profile.Value);

        if (key.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(key.Error);

        }

        key.Value.Dispose();

        Result<Guid> installation = ResolveInstallationIdentity(
            heldInstallationLock,
            guardedDirectory,
            profile.Value,
            request.ConflictMode,
            destinationInstallationId);

        if (installation.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(installation.Error);

        }

        Result<CovenantDigest> effect = ComputeEffectDigest(
            request,
            profile.Value,
            installation.Value,
            archiveManifestDigest,
            topology);

        if (effect.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(effect.Error);

        }

        CovenantExclusiveRecoveryOwner owner = new(
            operationId,
            CovenantExclusiveOperation.BackupRestore,
            effect.Value);

        Result<BackupRestoreJournalPayloadV2> payload = BuildPayload(
            owner,
            request,
            BackupRestorePhase.Stage,
            topology,
            markerCleanup: null);

        if (payload.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(payload.Error);

        }

        Result<BackupRestoreJournalLocation> location = _services.Anchors.ResolveLocation(
            profile.Value,
            installation.Value,
            operationId,
            StagingRootOf(topology));

        if (location.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(location.Error);

        }

        // Admission closes before the journal exists, because the journal is a record of an operation
        // that already owns the installation. Publishing one first would name an owner nothing holds.
        Result<CovenantExclusiveLease> acquired = await _services.Gate
            .AcquireExclusiveAsync(owner, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<BackupRestoreCovenantSession>.Failure(acquired.Error);

        }

        Result<BackupRestoreJournalPublication> published = _services.Anchors.Begin(
            heldInstallationLock,
            guardedDirectory,
            profile.Value,
            location.Value,
            payload.Value);

        if (published.IsFailure)
        {

            // Nothing durable happened, so the lease may reopen. This is the one abort that is proven
            // pre-swap by construction rather than by re-reading the tree.
            _ = await acquired.Value
                .CompleteAsync(
                    CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                    CovenantNoOpPostDispositionFinalizer.Instance,
                    CancellationToken.None)
                .ConfigureAwait(false);

            await acquired.Value.DisposeAsync().ConfigureAwait(false);

            return Result<BackupRestoreCovenantSession>.Failure(published.Error);

        }

        return new BackupRestoreCovenantSession(
            owner,
            acquired.Value,
            profile.Value,
            installation.Value,
            published.Value,
            payload.Value);

    }

    /// <summary>
    /// Reconciles the staged generation and commits this restore's marker children before displacement.
    /// </summary>
    /// <remarks>
    /// One staged core transaction carries the reconciliation and the cleanup children together, and it
    /// commits <em>before</em> the checkpoint is published. That order is what acceptance criterion four
    /// describes: a crash between the two leaves the old live root exactly where it was, and the retry
    /// re-enumerates the same same-owner rows, rebuilds the identical canonical receipt — including the
    /// proven-zero one — and publishes it before anything moves.
    /// </remarks>
    internal async Task<Result> ReconcileStagedAsync(
        BackupRestoreCovenantSession session,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        SqliteConnection staged,
        BackupCovenantRestoreDestinationState destination,
        BackupRestoreRequest request,
        BackupRestoreCovenantTopology topology,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(session);

        ArgumentNullException.ThrowIfNull(staged);

        Result<CampaignPathRestoreCleanupInventory> inventory = await _services.Markers
            .InventoryRestoreCleanupAsync(session.Owner, cancellationToken)
            .ConfigureAwait(false);

        if (inventory.IsFailure)
        {

            return inventory.Error;

        }

        Result<CampaignPathRestoreCleanupPreparationReceipt> receipt = await CommitStagedAsync(
            session,
            staged,
            destination,
            inventory.Value,
            cancellationToken).ConfigureAwait(false);

        if (receipt.IsFailure)
        {

            return receipt.Error;

        }

        BackupRestoreMarkerCleanupCheckpointV1 checkpoint = new(
            BackupRestoreMarkerCleanupCheckpointVersion,
            receipt.Value.Owner.OperationId,
            receipt.Value.Owner.Operation,
            receipt.Value.Owner.EffectDigest,
            receipt.Value.OrderedIntentIds,
            receipt.Value.IntentCount,
            receipt.Value.IntentVectorDigest);

        return Advance(
            session,
            heldInstallationLock,
            guardedDirectory,
            request,
            BackupRestorePhase.SafetyPoint,
            topology,
            checkpoint);

    }

    /// <summary>
    /// Advances the journal to the phase whose durable effect is about to happen, or has just happened.
    /// </summary>
    internal Result Advance(
        BackupRestoreCovenantSession session,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreRequest request,
        BackupRestorePhase phase,
        BackupRestoreCovenantTopology topology,
        BackupRestoreMarkerCleanupCheckpointV1? checkpoint = null)
    {

        ArgumentNullException.ThrowIfNull(session);

        Result<BackupRestoreJournalPayloadV2> payload = BuildPayload(
            session.Owner,
            request,
            phase,
            topology,
            checkpoint ?? session.Checkpoint);

        if (payload.IsFailure)
        {

            return payload.Error;

        }

        Result<BackupRestoreJournalPublication> published = _services.Anchors.Advance(
            heldInstallationLock,
            guardedDirectory,
            session.Profile,
            session.Publication,
            payload.Value);

        if (published.IsFailure)
        {

            return published.Error;

        }

        session.Publish(published.Value, payload.Value);

        return Result.Success();

    }

    /// <summary>
    /// Reconciles this restore's marker children in the new live database and reopens admission once.
    /// </summary>
    /// <remarks>
    /// The checkpoint is the only list of children this may touch. The lifecycle proves each one and
    /// returns the disposition rather than applying it, so anything short of a committed aggregate —
    /// a missing, extra, reordered, wrong-owner, or wrong-effect child, or an unexpected child beneath
    /// a proven zero checkpoint — leaves admission shut and the journal active.
    /// </remarks>
    internal async Task<Result> CompleteCommittedAsync(
        BackupRestoreCovenantSession session,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(session);

        if (session.Checkpoint is not { } checkpoint)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A restore that displaced the installation carries no marker cleanup checkpoint.");

        }

        Result<CampaignPathMarkerGateCompletion> completion = await _services.Markers
            .ReconcileGateOwnedAsync(
                new CampaignPathMarkerGateReconcileRequest(
                    session.Owner,
                    checkpoint.OrderedIntentIds,
                    checkpoint.IntentVectorDigest),
                session.Lease,
                cancellationToken)
            .ConfigureAwait(false);

        if (completion.IsFailure)
        {

            return completion.Error;

        }

        if (completion.Value.Outcome is not CampaignPathMarkerAggregateOutcome.Committed
            || completion.Value.Disposition is not CovenantExclusiveLeaseDisposition.CommitAndReopen)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This restore's marker children did not reach a committed disposition.");

        }

        // The sole general-admission reopen. It runs only after every child has been proven, and the
        // finalizer only after the disposition itself succeeded.
        Result committed = await session.Lease
            .CompleteAsync(completion.Value.Disposition, completion.Value.Finalizer, cancellationToken)
            .ConfigureAwait(false);

        if (committed.IsFailure)
        {

            return committed.Error;

        }

        session.MarkDispositioned();

        await _services.Markers.ReleaseRetainedRootsAsync(session.Owner.OperationId).ConfigureAwait(false);

        return _services.Anchors.Close(
            heldInstallationLock,
            guardedDirectory,
            session.Profile,
            session.Publication);

    }

    /// <summary>
    /// Ends a restore that did not commit, reopening only when nothing durable can have happened.
    /// </summary>
    /// <remarks>
    /// <paramref name="provenPreSwap"/> is the caller's filesystem evidence, not a guess: the service
    /// passes true only where it never began the two renames, or where the reversal put the prior
    /// installation verifiably back. Every other ending disposes the lease without a disposition, which
    /// is exactly <c>KeepClosed</c> — the closure and the owner survive, the journal stays active, and
    /// the next start resumes this operation.
    /// </remarks>
    internal async Task<Result> AbortAsync(
        BackupRestoreCovenantSession session,
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        bool provenPreSwap,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(session);

        if (session.Dispositioned)
        {

            return Result.Success();

        }

        if (!provenPreSwap)
        {

            await session.Lease.DisposeAsync().ConfigureAwait(false);

            await _services.Markers.ReleaseRetainedRootsAsync(session.Owner.OperationId)
                .ConfigureAwait(false);

            return Result.Success();

        }

        Result reopened = await session.Lease
            .CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                CovenantNoOpPostDispositionFinalizer.Instance,
                cancellationToken)
            .ConfigureAwait(false);

        if (reopened.IsFailure)
        {

            return reopened.Error;

        }

        session.MarkDispositioned();

        await _services.Markers.ReleaseRetainedRootsAsync(session.Owner.OperationId).ConfigureAwait(false);

        return _services.Anchors.Close(
            heldInstallationLock,
            guardedDirectory,
            session.Profile,
            session.Publication);

    }

    /// <summary>The version every checkpoint this build produces carries.</summary>
    internal const byte BackupRestoreMarkerCleanupCheckpointVersion = 1;

    /// <summary>
    /// Runs sanitation, reconciliation, and marker preparation, and commits them as one staged state.
    /// </summary>
    private async Task<Result<CampaignPathRestoreCleanupPreparationReceipt>> CommitStagedAsync(
        BackupRestoreCovenantSession session,
        SqliteConnection staged,
        BackupCovenantRestoreDestinationState destination,
        CampaignPathRestoreCleanupInventory inventory,
        CancellationToken cancellationToken)
    {

        Result<RestoreStagingManagedAuthoritySanitizationCapability> capability =
            RestoreStagingManagedAuthoritySanitizationCapability.Mint(
                _initializer,
                staged,
                session.Owner,
                await ReadStagedGenerationAsync(session, staged, cancellationToken).ConfigureAwait(false),
                _timeProvider,
                static () => true);

        if (capability.IsFailure)
        {

            return capability.Error;

        }

        await using (capability.Value.ConfigureAwait(false))
        {

            Result<BackupRestoreManagedAuthoritySanitizationReceipt> sanitized =
                await capability.Value.RunImmediateAsync(cancellationToken).ConfigureAwait(false);

            if (sanitized.IsFailure)
            {

                return sanitized.Error;

            }

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await staged.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Result<BackupCovenantRestoreReconciliationReceipt> reconciled =
            await BackupCovenantRestoreReconciler.ReconcileStagedAsync(
                staged,
                transaction,
                destination,
                _initializer,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);

        if (reconciled.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return reconciled.Error;

        }

        Result<CampaignPathRestoreCleanupPreparationReceipt> prepared = await _services.Markers
            .PrepareRestoreCleanupInStagedDatabaseAsync(
                new CampaignPathRestoreCleanupPreparation(session.Owner, inventory.OrderedSeeds),
                staged,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return prepared.Error;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return prepared;

    }

    /// <summary>
    /// The generation the stripped managed authority actually belonged to.
    /// </summary>
    /// <remarks>
    /// Read before the reconciliation reissues it, so a tombstone names the dataset its source rows
    /// were written under rather than the one that replaces them. That also makes the value stable
    /// across a retry over the same staged database, which is what lets the sanitizer's own guards
    /// recognise their earlier output.
    ///
    /// <para>A staged snapshot with no canonical tier has no generation to name, and the restore's own
    /// operation identity stands in — it is unique per restore and cannot collide with a real dataset,
    /// because a database that has one is never on this branch.</para>
    /// </remarks>
    private static async Task<Guid> ReadStagedGenerationAsync(
        BackupRestoreCovenantSession session,
        SqliteConnection staged,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(staged, "covenant_state", cancellationToken)
                .ConfigureAwait(false))
        {

            return session.Owner.OperationId;

        }

        await using SqliteCommand command = staged.CreateCommand();

        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {

            return session.Owner.OperationId;

        }

        using Stream stream = reader.GetStream(0);

        using MemoryStream buffer = new();

        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return buffer.Length == 16
            ? new Guid(buffer.ToArray(), bigEndian: true)
            : session.Owner.OperationId;

    }

    private Result<Guid> ResolveInstallationIdentity(
        ArcanumMaintenanceLock heldInstallationLock,
        string guardedDirectory,
        BackupRestoreProfileNamespace profile,
        BackupRestoreConflictMode conflictMode,
        Guid destinationInstallationId)
    {

        if (conflictMode is BackupRestoreConflictMode.NewProfileRoot)
        {

            return _services.Identities.CreateForNewProfileRoot(
                heldInstallationLock,
                guardedDirectory,
                profile);

        }

        if (destinationInstallationId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This installation's authority row names no identity to bind the restore journal to.");

        }

        // Seeds when absent and compares when present, which is the same call: the restore is the first
        // operation that needs this binding, so it is also the first that may create it (§10.19.6).
        return _services.Identities.SeedFromDatabase(
            heldInstallationLock,
            guardedDirectory,
            profile,
            destinationInstallationId);

    }

    private Result<CovenantDigest> ComputeEffectDigest(
        BackupRestoreRequest request,
        BackupRestoreProfileNamespace profile,
        Guid installationId,
        CovenantDigest archiveManifestDigest,
        BackupRestoreCovenantTopology topology)
    {

        Result<CovenantDigest> archive = NodeIdentity(topology.ArchivePath);

        if (archive.IsFailure)
        {

            return archive;

        }

        Result<CovenantDigest> mappings = BackupRestoreEffectDigestCalculator.PathMappingVector(
            [.. request.PathMappings ?? []]);

        if (mappings.IsFailure)
        {

            return mappings;

        }

        string destinationRoot = request.ConflictMode is BackupRestoreConflictMode.NewProfileRoot
            ? request.DestinationRoot ?? string.Empty
            : topology.LiveRoot;

        Result<CovenantDigest> destination = DestinationRootIdentity(destinationRoot);

        if (destination.IsFailure)
        {

            return destination;

        }

        return _services.EffectDigests.Compute(
            new BackupRestoreEffectDigestInput(
                archiveManifestDigest,
                archive.Value,
                profile.Digest,
                installationId,
                destination.Value,
                request.ConflictMode,
                request.ProtectedStateMode,
                mappings.Value,
                request.RestoreMasterApiKey,
                request.CreateSafetyBackup));

    }

    /// <summary>
    /// Builds the payload for one phase, capturing every node's identity from its own handle.
    /// </summary>
    private Result<BackupRestoreJournalPayloadV2> BuildPayload(
        CovenantExclusiveRecoveryOwner owner,
        BackupRestoreRequest request,
        BackupRestorePhase phase,
        BackupRestoreCovenantTopology topology,
        BackupRestoreMarkerCleanupCheckpointV1? markerCleanup)
    {

        Result<BackupRestoreDurableNodeIdentityV1> live = CaptureDirectory(topology.LiveRoot);

        if (live.IsFailure)
        {

            return Result<BackupRestoreJournalPayloadV2>.Failure(live.Error);

        }

        Result<BackupRestoreDurableNodeIdentityV1> staged = CaptureDirectory(topology.StagedRoot);

        if (staged.IsFailure)
        {

            return Result<BackupRestoreJournalPayloadV2>.Failure(staged.Error);

        }

        Result<BackupRestoreDurableNodeIdentityV1> displaced = CaptureDirectory(topology.DisplacedRoot);

        if (displaced.IsFailure)
        {

            return Result<BackupRestoreJournalPayloadV2>.Failure(displaced.Error);

        }

        Result<BackupRestoreDurableNodeIdentityV1> archive = CaptureFile(topology.ArchivePath);

        if (archive.IsFailure)
        {

            return Result<BackupRestoreJournalPayloadV2>.Failure(archive.Error);

        }

        BackupRestoreJournalPayloadV2 payload = new(
            owner.OperationId,
            owner.Operation,
            owner.EffectDigest,
            request.ConflictMode,
            phase,
            RequestDigest(request),
            live.Value,
            staged.Value,
            displaced.Value,
            archive.Value,
            SafetyBackup: null,
            markerCleanup);

        Result validated = BackupRestoreJournalAuthenticator.ValidatePayload(payload);

        return validated.IsFailure
            ? Result<BackupRestoreJournalPayloadV2>.Failure(validated.Error)
            : payload;

    }

    /// <summary>
    /// A one-way commitment to what the operator asked for, with no path text in it.
    /// </summary>
    private static CovenantDigest RequestDigest(BackupRestoreRequest request)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(RequestDigestDomain));

        hash.AppendData([0x00, (byte)request.ConflictMode, (byte)request.ProtectedStateMode]);

        hash.AppendData(
        [
            request.RestoreMasterApiKey ? (byte)1 : (byte)0,
            request.CreateSafetyBackup ? (byte)1 : (byte)0,
        ]);

        Span<byte> count = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(
            count,
            checked((ulong)(request.PathMappings?.Length ?? 0)));

        hash.AppendData(count);

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static string StagingRootOf(BackupRestoreCovenantTopology topology) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(topology.StagedRoot)))
        ?? throw new InvalidOperationException("A staged root always sits inside its staging root.");

    private static Result<BackupRestoreDurableNodeIdentityV1> CaptureDirectory(string path) =>
        Capture(path, BackupRestoreNodeKind.Directory);

    private static Result<BackupRestoreDurableNodeIdentityV1> CaptureFile(string path) =>
        Capture(path, BackupRestoreNodeKind.RegularFile);

    /// <summary>
    /// Records one node the way physical recovery is allowed to reopen it.
    /// </summary>
    /// <remarks>
    /// The parent's identity is captured from its own no-follow handle and the child is recorded as a
    /// bare leaf, never as a path recovery could resolve on its own. A node that is not there yet is
    /// recorded as absent with no identity at all, because a nullable identity standing in for both
    /// would make "I did not look" and "it was not there" the same durable claim.
    /// </remarks>
    private static Result<BackupRestoreDurableNodeIdentityV1> Capture(
        string path,
        BackupRestoreNodeKind kind)
    {

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        string? parent = Path.GetDirectoryName(full);

        string leaf = Path.GetFileName(full);

        if (string.IsNullOrEmpty(parent) || leaf.Length == 0)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journalled restore node requires a parent directory and a bounded child leaf.");

        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(parent, out FileHandleMetadata metadata)
            || metadata.Kind is not FileSystemObjectKind.Directory)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "A journalled restore node's parent directory could not be identified.");

        }

        CovenantDigest parentIdentity = BackupRestoreJournalAuthenticator.PhysicalIdentity(
            metadata.Identity.VolumeId,
            metadata.Identity.FileId);

        bool present = FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                full,
                out FileHandleMetadata child)
            && child.Kind == (kind is BackupRestoreNodeKind.Directory
                ? FileSystemObjectKind.Directory
                : FileSystemObjectKind.RegularFile);

        return new BackupRestoreDurableNodeIdentityV1(
            parent,
            parentIdentity,
            leaf,
            kind,
            present ? BackupRestoreNodePresence.Present : BackupRestoreNodePresence.Absent,
            present
                ? BackupRestoreJournalAuthenticator.PhysicalIdentity(
                    child.Identity.VolumeId,
                    child.Identity.FileId)
                : null,
            null);

    }

    private static Result<CovenantDigest> NodeIdentity(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
            ? BackupRestoreJournalAuthenticator.PhysicalIdentity(
                metadata.Identity.VolumeId,
                metadata.Identity.FileId)
            : new Error(
                ErrorCodes.Covenant.Unavailable,
                "The archive this restore reads could not be identified through its own handle.");

    /// <summary>
    /// The destination identity the effect digest binds: parent, leaf, and whether the root is there.
    /// </summary>
    private static Result<CovenantDigest> DestinationRootIdentity(string destinationRoot)
    {

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore effect digest requires the destination root it authorizes.");

        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));

        string? parent = Path.GetDirectoryName(full);

        if (string.IsNullOrEmpty(parent)
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                parent,
                out FileHandleMetadata metadata))
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The destination root's parent could not be identified through its own handle.");

        }

        return BackupRestoreEffectDigestCalculator.DestinationRootIdentity(
            BackupRestoreJournalAuthenticator.PhysicalIdentity(
                metadata.Identity.VolumeId,
                metadata.Identity.FileId),
            Path.GetFileName(full),
            Directory.Exists(full));

    }

}
