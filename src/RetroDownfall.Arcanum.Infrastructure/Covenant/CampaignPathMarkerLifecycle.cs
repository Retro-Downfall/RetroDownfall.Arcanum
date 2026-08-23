using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using Serilog;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one implementation of the Campaign marker protocol.
/// </summary>
/// <remarks>
/// Preparation observes each marker through an already-opened root, commits a one-way digest of the
/// exact bytes it saw, and retains that root. Reconciliation re-reads through the same retained root,
/// proves the bytes still hash to the committed digest, and only then compare-deletes. Nothing in
/// this file resolves a display path: the single path resolution happened when the root was opened,
/// so a display path that becomes a symlink in between redirects nothing (§10.12).
///
/// <para>A process that retained nothing — a restart — has one more step, and it lives in
/// <c>CampaignPathMarkerLifecycle.RestartRootProof.cs</c> so that this file stays incapable of the
/// resolution rather than merely declining it. That arm reopens the recorded path once and believes
/// the directory it found only after the marker inside it derives that directory's exact identity
/// from the root tuple the marker binds itself to (§10.19.7).</para>
///
/// <para>The marker bytes themselves are never stored. A cleanup that had to keep the payload in the
/// journal would be storing a per-registration secret in order to delete a file, and the digest plus
/// a re-read proves the same thing without holding the secret at all.</para>
/// </remarks>
internal sealed partial class CampaignPathMarkerLifecycle
    : ICampaignPathMarkerLifecycle, IAsyncDisposable
{

    private readonly ICampaignPathMarkerCodec _codec;

    /// <summary>
    /// The sole producer of Campaign root identity, used only where no root was retained.
    /// </summary>
    /// <remarks>
    /// Present for the restart arm in <c>CampaignPathMarkerLifecycle.RestartRootProof.cs</c> and
    /// unreachable from this file, so the retained-root half of the protocol stays incapable of
    /// resolving a display path a second time rather than merely uninterested in doing so.
    /// </remarks>
    private readonly PhysicalCampaignRootOpener _rootOpener;

    private readonly ICovenantConnectionSource _liveConnections;

    private readonly CovenantSqliteConnectionInitializer _initializer;

    private readonly TimeProvider _timeProvider;

    private readonly ICampaignRootIdentityRecoveryKeyProvider? _recoveryKeys;

    /// <summary>
    /// Roots this process opened during preparation, keyed by the child they belong to.
    /// </summary>
    /// <remarks>
    /// Retained rather than reopened. A reconciliation that resolved the display path again would be
    /// trusting a name the operating system may now resolve somewhere else, and the whole marker
    /// protocol exists because that name is not authority.
    /// </remarks>
    private readonly ConcurrentDictionary<Guid, RetainedRoot> _retainedRoots = new();

    /// <summary>
    /// Roots retained before full-reset marker child identifiers exist.
    /// </summary>
    private readonly ConcurrentDictionary<FullResetRetainedRootKey, CampaignPathMarkerRootAuthority>
        _fullResetRetainedRoots = new();

    private readonly object _retainedRootsGate = new();

    private bool _retainedRootsDisposed;

    internal CampaignPathMarkerLifecycle(
        ICampaignPathMarkerCodec codec,
        PhysicalCampaignRootOpener rootOpener,
        ICovenantConnectionSource liveConnections,
        CovenantSqliteConnectionInitializer initializer,
        TimeProvider timeProvider,
        ICampaignRootIdentityRecoveryKeyProvider? recoveryKeys = null)
    {

        _codec = codec ?? throw new ArgumentNullException(nameof(codec));

        _rootOpener = rootOpener ?? throw new ArgumentNullException(nameof(rootOpener));

        _liveConnections = liveConnections ?? throw new ArgumentNullException(nameof(liveConnections));

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _recoveryKeys = recoveryKeys;

    }

    public async Task<Result<CampaignPathRestoreCleanupPreparationReceipt>>
        PrepareRestoreCleanupInStagedDatabaseAsync(
            CampaignPathRestoreCleanupPreparation preparation,
            SqliteConnection stagedConnection,
            SqliteTransaction stagedTransaction,
            CancellationToken cancellationToken)
    {

        if (preparation is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore cleanup preparation is required.");

        }

        Result ownerValid = ValidateRestoreOwner(preparation.Owner);

        if (ownerValid.IsFailure)
        {

            return ownerValid.Error;

        }

        if (preparation.OrderedSeeds.IsDefault)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An uninitialized seed vector is not a proven empty marker inventory.");

        }

        if (preparation.OrderedSeeds.Length > CampaignPathRestoreCleanupIntentVector.MaximumIntents)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This restore observed more Campaign markers than one authenticated journal may carry.");

        }

        // The zero arm never opens marker storage, borrows mutation authorization, calls the codec, or
        // touches the filesystem. That is what makes a proven empty inventory distinguishable from a
        // preparation that never ran: it produces the one frozen digest and nothing else could.
        if (preparation.OrderedSeeds.IsEmpty)
        {

            return new CampaignPathRestoreCleanupPreparationReceipt(
                preparation.Owner,
                ImmutableArray<Guid>.Empty,
                0,
                CampaignPathRestoreCleanupIntentVector.EmptyDigest);

        }

        if (stagedConnection is null || stagedTransaction is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A nonempty restore cleanup preparation requires the caller's staged transaction.");

        }

        HashSet<Guid> campaigns = new(preparation.OrderedSeeds.Length);

        foreach (CampaignPathRestoreCleanupSeed seed in preparation.OrderedSeeds)
        {

            Result seedValid = ValidateSeed(seed, campaigns);

            if (seedValid.IsFailure)
            {

                return seedValid.Error;

            }

        }

        CampaignPathMarkerIntentStore store = new(
            _initializer,
            stagedConnection,
            stagedTransaction,
            _timeProvider);

        ImmutableArray<Guid>.Builder committed =
            ImmutableArray.CreateBuilder<Guid>(preparation.OrderedSeeds.Length);

        foreach (CampaignPathRestoreCleanupSeed seed in preparation.OrderedSeeds)
        {

            CampaignPathMarkerRootAuthority authority =
                ((CampaignPathCleanupRootObservation.Opened)seed.Observation).RootAuthority;

            Result<ObservedMarker> observed = await ObserveMarkerAsync(
                authority,
                seed,
                cancellationToken).ConfigureAwait(false);

            if (observed.IsFailure)
            {

                return observed.Error;

            }

            // A proven-absent marker needs no cleanup effect and therefore no durable child. Recording
            // one would put a row in the checkpoint whose only possible outcome is "there was nothing
            // to do", and a sentinel digest standing in for "no bytes" is one substitution away from
            // matching a real marker.
            if (observed.Value.Absent)
            {

                continue;

            }

            Result<Guid> inserted = await store.InsertOrReadRestoreCleanupAsync(
                preparation.Owner.OperationId,
                preparation.Owner.EffectDigest,
                seed.CampaignId,
                observed.Value.MarkerDigest,
                seed.CanonicalDisplayPath,
                seed.PriorPathRevision,
                cancellationToken).ConfigureAwait(false);

            if (inserted.IsFailure)
            {

                return inserted.Error;

            }

            RetainedRoot retained = new(
                preparation.Owner.OperationId,
                authority);

            if (!TryRetainRoot(inserted.Value, retained, out RetainedRoot? displaced))
            {

                await authority.DisposeAsync().ConfigureAwait(false);

                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "Campaign marker lifecycle authority is no longer available.");

            }

            if (displaced is not null)
            {

                await CampaignPathRetainedRootRelease.DisposeAllAsync(
                    [displaced.Authority]).ConfigureAwait(false);

            }

            committed.Add(inserted.Value);

        }

        ImmutableArray<Guid> orderedIntentIds = committed.ToImmutable();

        Result<CovenantDigest> vector =
            CampaignPathRestoreCleanupIntentVector.Compute(orderedIntentIds);

        if (vector.IsFailure)
        {

            return vector.Error;

        }

        return new CampaignPathRestoreCleanupPreparationReceipt(
            preparation.Owner,
            orderedIntentIds,
            checked((ulong)orderedIntentIds.Length),
            vector.Value);

    }

    public async Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
        CampaignPathMarkerGateReconcileRequest request,
        ICovenantExclusiveOperationLease exclusiveLease,
        CancellationToken cancellationToken)
    {

        if (request is null || exclusiveLease is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A gate-owned marker reconciliation requires its request and the caller's lease.");

        }

        Result ownerValid = ValidateRestoreOwner(request.Owner);

        if (ownerValid.IsFailure)
        {

            return ownerValid.Error;

        }

        CovenantOperationLeaseSnapshot snapshot = exclusiveLease.Snapshot;

        if (snapshot.RecoveryOwner is not { } held
            || held.OperationId != request.Owner.OperationId
            || held.Operation != request.Owner.Operation
            || held.EffectDigest != request.Owner.EffectDigest
            || snapshot.Coverage != CovenantLeaseCoverage.Installation)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "The supplied lease does not close this restore's scope under this exact owner.");

        }

        Result<CovenantDigest> recomputed =
            CampaignPathRestoreCleanupIntentVector.Compute(request.OrderedIntentIds);

        if (recomputed.IsFailure)
        {

            return recomputed.Error;

        }

        if (recomputed.Value != request.IntentVectorDigest)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The named marker children do not reproduce the authenticated intent-vector digest.");

        }

        // The verified zero arm consumes its frozen values and constructs no substitutes. It opens no
        // marker store, borrows no mutation authorization, calls no codec, and makes no filesystem
        // call, because a proven empty inventory has nothing to reconcile.
        if (request.OrderedIntentIds.IsEmpty)
        {

            return request.IntentVectorDigest == CampaignPathRestoreCleanupIntentVector.EmptyDigest
                ? new CampaignPathMarkerGateCompletion(
                    CampaignPathMarkerAggregateOutcome.Committed,
                    CovenantExclusiveLeaseDisposition.CommitAndReopen,
                    CovenantNoOpPostDispositionFinalizer.Instance)
                : new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A zero-child marker checkpoint must carry the frozen empty-vector digest.");

        }

        SqliteConnection connection =
            await _liveConnections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        foreach (Guid intentId in request.OrderedIntentIds)
        {

            Result advanced = await ReconcileOneAsync(
                connection,
                request.Owner,
                intentId,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return advanced.Error;

            }

        }

        // An extra same-owner child beneath a proven checkpoint means the durable state and the
        // authenticated vector disagree about what this restore prepared. Reopening admission on the
        // strength of the shorter of the two would abandon a marker nothing else will ever adopt.
        long ownerChildren = await CountOwnerChildrenAsync(
            connection,
            request.Owner.OperationId,
            cancellationToken).ConfigureAwait(false);

        if (ownerChildren != request.OrderedIntentIds.Length)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The live database holds a different number of children than this restore journaled.");

        }

        return new CampaignPathMarkerGateCompletion(
            CampaignPathMarkerAggregateOutcome.Committed,
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            new MarkerChildCompletionFinalizer(
                this,
                connection,
                request.Owner,
                request.OrderedIntentIds));

    }

    public async ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId)
    {

        List<IAsyncDisposable> removedRoots;

        lock (_retainedRootsGate)
        {

            removedRoots = DetachRetainedRoots(ownerOperationId);

        }

        await CampaignPathRetainedRootRelease.DisposeAllAsync(removedRoots)
            .ConfigureAwait(false);

    }

    public async ValueTask DisposeAsync()
    {

        List<IAsyncDisposable> removedRoots;

        lock (_retainedRootsGate)
        {

            if (_retainedRootsDisposed)
            {

                return;

            }

            _retainedRootsDisposed = true;

            removedRoots = DetachRetainedRoots(ownerOperationId: null);

        }

        await CampaignPathRetainedRootRelease.DisposeAllAsync(removedRoots)
            .ConfigureAwait(false);

    }

    private bool TryRetainRoot(
        Guid intentId,
        RetainedRoot retained,
        out RetainedRoot? displaced)
    {

        lock (_retainedRootsGate)
        {

            displaced = null;

            if (_retainedRootsDisposed)
            {

                return false;

            }

            if (_retainedRoots.TryGetValue(intentId, out RetainedRoot? current)
                && !ReferenceEquals(current.Authority, retained.Authority))
            {

                displaced = current;

            }

            _retainedRoots[intentId] = retained;

            return true;

        }

    }

    private bool TryRetainFullResetRoot(
        FullResetRetainedRootKey retainedKey,
        CampaignPathMarkerRootAuthority authority)
    {

        lock (_retainedRootsGate)
        {

            if (_retainedRootsDisposed)
            {

                return false;

            }

            if (_fullResetRetainedRoots.TryGetValue(
                    retainedKey,
                    out CampaignPathMarkerRootAuthority? current)
                && ReferenceEquals(current, authority))
            {

                return true;

            }

            return _fullResetRetainedRoots.TryAdd(retainedKey, authority);

        }

    }

    private List<IAsyncDisposable> DetachRetainedRoots(Guid? ownerOperationId)
    {

        List<IAsyncDisposable> removedRoots = [];

        foreach (KeyValuePair<Guid, RetainedRoot> entry in _retainedRoots)
        {

            if (ownerOperationId is { } owner
                && entry.Value.OwnerOperationId != owner)
            {

                continue;

            }

            if (_retainedRoots.TryRemove(entry.Key, out RetainedRoot? removed))
            {

                removedRoots.Add(removed.Authority);

            }

        }

        foreach (KeyValuePair<FullResetRetainedRootKey, CampaignPathMarkerRootAuthority> entry
            in _fullResetRetainedRoots)
        {

            if (ownerOperationId is { } owner
                && entry.Key.OwnerOperationId != owner)
            {

                continue;

            }

            if (_fullResetRetainedRoots.TryRemove(
                    entry.Key,
                    out CampaignPathMarkerRootAuthority? removed))
            {

                removedRoots.Add(removed);

            }

        }

        return removedRoots;

    }

    private static Result ValidateRestoreOwner(CovenantExclusiveRecoveryOwner owner) =>
        owner.IsValid && owner.Operation == CovenantExclusiveOperation.BackupRestore
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "Restore cleanup children are owned only by a complete BackupRestore owner."));

    private static Result ValidateSeed(
        CampaignPathRestoreCleanupSeed seed,
        HashSet<Guid> campaigns)
    {

        if (seed is null
            || seed.CampaignId == Guid.Empty
            || seed.PriorPathRevision <= 0
            || !seed.IndexedIdentityDigest.IsValid
            || string.IsNullOrWhiteSpace(seed.CanonicalDisplayPath))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore cleanup seed requires a complete Campaign identity and path evidence.");

        }

        if (!campaigns.Add(seed.CampaignId))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "One restore may hold at most one cleanup child per Campaign.");

        }

        // Restore requires an opened root for every seed. A blocked root cannot be cleaned, and
        // continuing past it would displace the live installation while leaving a marker that claims
        // a Campaign the restored installation does not own.
        if (seed.Observation is not CampaignPathCleanupRootObservation.Opened opened)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A restore cannot proceed while a destination Campaign root cannot be opened.");

        }

        return opened.RootAuthority.CampaignId == seed.CampaignId
            && opened.RootAuthority.PathRevision == seed.PriorPathRevision
            && opened.RootAuthority.PhysicalIdentityDigest == seed.IndexedIdentityDigest
            ? Result.Success()
            : Result.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A restore cleanup seed does not echo the root authority it carries."));

    }

    private async Task<Result<ObservedMarker>> ObserveMarkerAsync(
        CampaignPathMarkerRootAuthority authority,
        CampaignPathRestoreCleanupSeed seed,
        CancellationToken cancellationToken)
    {

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(cancellationToken)
                .ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return opened.Error;

        }

        if (opened.Value is PhysicalCampaignMarkerOpenResult.Absent)
        {

            return new ObservedMarker(Absent: true, default, default);

        }

        PhysicalCampaignRootOpener.MarkerHandleCapability marker =
            ((PhysicalCampaignMarkerOpenResult.Opened)opened.Value).Marker;

        await using (marker.ConfigureAwait(false))
        {

            Result<PhysicalCampaignRootOpener.MarkerCodecBytesLease> read =
                await marker.ReadAllBoundedAsync(
                    _codec.MaximumMarkerByteCount,
                    cancellationToken).ConfigureAwait(false);

            if (read.IsFailure)
            {

                return read.Error;

            }

            using PhysicalCampaignRootOpener.MarkerCodecBytesLease lease = read.Value;

            Result<CampaignPathMarkerContent> parsed = _codec.Parse(lease.Bytes.Span);

            if (parsed.IsFailure)
            {

                return parsed.Error;

            }

            // The marker has to agree with the row it is about to authorize a delete for. A marker
            // naming a different Campaign or revision is somebody else's evidence, and removing it
            // would destroy a claim this restore has no authority over.
            if (parsed.Value.CampaignId != seed.CampaignId
                || parsed.Value.PathRevision != seed.PriorPathRevision)
            {

                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "A destination Campaign root carries a marker that names different ownership.");

            }

            return new ObservedMarker(
                Absent: false,
                new CovenantDigest(SHA256.HashData(lease.Bytes.Span)),
                marker.PhysicalIdentityDigest);

        }

    }

    private async Task<Result> ReconcileOneAsync(
        SqliteConnection connection,
        CovenantExclusiveRecoveryOwner owner,
        Guid intentId,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        CampaignPathMarkerIntentStore store = new(_initializer, connection, transaction, _timeProvider);

        CampaignPathMarkerIntentRow? row =
            await store.ReadAsync(intentId, cancellationToken).ConfigureAwait(false);

        if (row is null
            || row.OwnerOperationId != owner.OperationId
            || row.OwnerEffectDigest != owner.EffectDigest
            || row.Kind != CampaignPathMarkerIntentKind.RestoreCleanup
            || row.ExclusiveOwnerOperation != CovenantExclusiveOperation.BackupRestore)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A named marker child is missing or does not belong to this restore owner.");

        }

        if (row.Phase is CampaignPathMarkerPhase.ReopenPending)
        {

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();

        }

        if (row.Phase is not CampaignPathMarkerPhase.Prepared
            and not CampaignPathMarkerPhase.TargetReopenedOrAbsent
            and not CampaignPathMarkerPhase.CodecOrAbsenceVerified
            and not CampaignPathMarkerPhase.DatabaseStateCommitted
            and not CampaignPathMarkerPhase.SensitiveMaterialDestroyed)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "A marker child is in a phase automatic reconciliation may not adopt.");

        }

        if (!_retainedRoots.TryGetValue(intentId, out RetainedRoot? retained)
            || retained.OwnerOperationId != owner.OperationId)
        {

            // A restart lost every handle preparation opened. The intent row still records no root
            // identity digest, so the proof comes from the marker instead: it binds itself to the
            // volume and file identifiers of the root it was written into.
            Result<RetainedRoot> reopened =
                await ReopenAndProveRootAsync(owner, row, cancellationToken).ConfigureAwait(false);

            if (reopened.IsFailure)
            {

                return reopened.Error;

            }

            retained = reopened.Value;

        }

        Result effect = await ApplyCleanupEffectAsync(
            store,
            retained.Authority,
            row,
            cancellationToken).ConfigureAwait(false);

        if (effect.IsFailure)
        {

            return effect.Error;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    private async Task<Result> ApplyCleanupEffectAsync(
        CampaignPathMarkerIntentStore store,
        CampaignPathMarkerRootAuthority authority,
        CampaignPathMarkerIntentRow row,
        CancellationToken cancellationToken)
    {

        long revision = row.PhaseRevision;

        Result<PhysicalCampaignMarkerOpenResult> opened =
            await authority.OpenMarkerOrProveAbsentNoFollowAsync(cancellationToken)
                .ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return opened.Error;

        }

        if (opened.Value is PhysicalCampaignMarkerOpenResult.Absent)
        {

            // Absence here is a completed delete, not a missing target: preparation proved the marker
            // present, and nothing but this operation holds authority over that directory.
            return await AdvanceToPendingAsync(
                store,
                row.IntentId,
                revision,
                new CampaignPathMarkerTargetObservation(
                    CampaignPathMarkerTargetObservationCode.Absent,
                    null),
                cancellationToken).ConfigureAwait(false);

        }

        PhysicalCampaignRootOpener.MarkerHandleCapability marker =
            ((PhysicalCampaignMarkerOpenResult.Opened)opened.Value).Marker;

        await using (marker.ConfigureAwait(false))
        {

            Result<PhysicalCampaignRootOpener.MarkerCodecBytesLease> read =
                await marker.ReadAllBoundedAsync(
                    _codec.MaximumMarkerByteCount,
                    cancellationToken).ConfigureAwait(false);

            if (read.IsFailure)
            {

                return read.Error;

            }

            using PhysicalCampaignRootOpener.MarkerCodecBytesLease lease = read.Value;

            // The committed digest is the whole authority for this delete. Bytes that no longer hash
            // to it are a different file than the one this restore journaled, and the correct answer
            // is to leave it exactly where it is.
            if (new CovenantDigest(SHA256.HashData(lease.Bytes.Span)) != row.MarkerDigest)
            {

                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "The Campaign root marker changed after this restore committed its evidence.");

            }

            Result observed = await store.AdvancePhaseAsync(
                row.IntentId,
                revision,
                CampaignPathMarkerPhase.TargetReopenedOrAbsent,
                new CampaignPathMarkerTargetObservation(
                    CampaignPathMarkerTargetObservationCode.Opened,
                    marker.PhysicalIdentityDigest),
                null,
                cancellationToken).ConfigureAwait(false);

            if (observed.IsFailure)
            {

                return observed.Error;

            }

            revision++;

            Result verified = await store.AdvancePhaseAsync(
                row.IntentId,
                revision,
                CampaignPathMarkerPhase.CodecOrAbsenceVerified,
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure)
            {

                return verified.Error;

            }

            revision++;

            Result deleted = await authority.CompareDeleteMarkerAsync(
                marker,
                marker.PhysicalIdentityDigest,
                lease.Bytes,
                cancellationToken).ConfigureAwait(false);

            if (deleted.IsFailure)
            {

                return deleted.Error;

            }

            Result flushed = await authority.FlushMarkerDirectoryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (flushed.IsFailure)
            {

                return flushed.Error;

            }

        }

        Result committed = await store.AdvancePhaseAsync(
            row.IntentId,
            revision,
            CampaignPathMarkerPhase.DatabaseStateCommitted,
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        if (committed.IsFailure)
        {

            return committed.Error;

        }

        revision++;

        Result destroyed = await store.AdvancePhaseAsync(
            row.IntentId,
            revision,
            CampaignPathMarkerPhase.SensitiveMaterialDestroyed,
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        if (destroyed.IsFailure)
        {

            return destroyed.Error;

        }

        revision++;

        return await store.AdvancePhaseAsync(
            row.IntentId,
            revision,
            CampaignPathMarkerPhase.ReopenPending,
            null,
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Walks a proven-absent target straight through the remaining phases to its pending disposition.
    /// </summary>
    private async Task<Result> AdvanceToPendingAsync(
        CampaignPathMarkerIntentStore store,
        Guid intentId,
        long phaseRevision,
        CampaignPathMarkerTargetObservation observation,
        CancellationToken cancellationToken)
    {

        CampaignPathMarkerPhase[] ladder =
        [
            CampaignPathMarkerPhase.TargetReopenedOrAbsent,
            CampaignPathMarkerPhase.CodecOrAbsenceVerified,
            CampaignPathMarkerPhase.DatabaseStateCommitted,
            CampaignPathMarkerPhase.SensitiveMaterialDestroyed,
            CampaignPathMarkerPhase.ReopenPending,
        ];

        long revision = phaseRevision;

        foreach (CampaignPathMarkerPhase phase in ladder)
        {

            Result advanced = await store.AdvancePhaseAsync(
                intentId,
                revision,
                phase,
                phase == CampaignPathMarkerPhase.TargetReopenedOrAbsent ? observation : null,
                phase == CampaignPathMarkerPhase.ReopenPending
                    ? CovenantExclusiveLeaseDisposition.CommitAndReopen
                    : null,
                cancellationToken).ConfigureAwait(false);

            if (advanced.IsFailure)
            {

                return advanced.Error;

            }

            revision++;

        }

        return Result.Success();

    }

    private async Task<long> CountOwnerChildrenAsync(
        SqliteConnection connection,
        Guid ownerOperationId,
        CancellationToken cancellationToken)
    {

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        CampaignPathMarkerIntentStore store = new(_initializer, connection, transaction, _timeProvider);

        long count = await store.CountForOwnerAsync(ownerOperationId, cancellationToken)
            .ConfigureAwait(false);

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return count;

    }

    private sealed record RetainedRoot(
        Guid OwnerOperationId,
        CampaignPathMarkerRootAuthority Authority);

    private readonly record struct FullResetRetainedRootKey(
        Guid OwnerOperationId,
        Guid CampaignId);

    private readonly record struct ObservedMarker(
        bool Absent,
        CovenantDigest MarkerDigest,
        CovenantDigest MarkerPhysicalIdentityDigest);

    /// <summary>
    /// Advances every pending child to <see cref="CampaignPathMarkerPhase.Completed"/>, and only after
    /// the caller's disposition has actually succeeded.
    /// </summary>
    /// <remarks>
    /// One-shot and never invoked by the lifecycle. A finalizer that ran before the disposition would
    /// record a terminal phase nobody proved, and the operation would stop being resumable at exactly
    /// the moment resuming it was still the correct answer.
    /// </remarks>
    private sealed class MarkerChildCompletionFinalizer(
        CampaignPathMarkerLifecycle owner,
        SqliteConnection connection,
        CovenantExclusiveRecoveryOwner recoveryOwner,
        ImmutableArray<Guid> orderedIntentIds)
        : ICovenantExclusivePostDispositionFinalizer
    {

        private int _invoked;

        public async ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {

            if (Interlocked.Exchange(ref _invoked, 1) != 0)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "This marker journal finalizer has already run."));

            }

            if (disposition != CovenantExclusiveLeaseDisposition.CommitAndReopen)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.LifecycleConflict,
                        "Restore cleanup children complete only after a committing disposition."));

            }

            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

            CampaignPathMarkerIntentStore store = new(
                owner._initializer,
                connection,
                transaction,
                owner._timeProvider);

            foreach (Guid intentId in orderedIntentIds)
            {

                CampaignPathMarkerIntentRow? row =
                    await store.ReadAsync(intentId, cancellationToken).ConfigureAwait(false);

                if (row is null || row.Phase != CampaignPathMarkerPhase.ReopenPending)
                {

                    return Result.Failure(
                        new Error(
                            ErrorCodes.Covenant.LifecycleConflict,
                            "A marker child left its pending phase before finalization."));

                }

                Result completed = await store.AdvancePhaseAsync(
                    intentId,
                    row.PhaseRevision,
                    CampaignPathMarkerPhase.Completed,
                    null,
                    null,
                    cancellationToken).ConfigureAwait(false);

                if (completed.IsFailure)
                {

                    return completed;

                }

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            await owner.ReleaseRetainedRootsAsync(recoveryOwner.OperationId).ConfigureAwait(false);

            return Result.Success();

        }

    }

}

internal static class CampaignPathRetainedRootRelease
{

    internal static async ValueTask DisposeAllAsync(
        IEnumerable<IAsyncDisposable> retainedRoots)
    {

        foreach (IAsyncDisposable retainedRoot in retainedRoots)
        {

            try
            {

                await retainedRoot.DisposeAsync().ConfigureAwait(false);

            }
            catch (Exception exception)
            {

                Log.Warning(
                    exception,
                    "A retained Campaign root authority reported a disposal failure; remaining roots will still be released.");

            }

        }

    }

}
