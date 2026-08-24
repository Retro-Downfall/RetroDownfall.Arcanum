using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>
/// The one stopped-host entry point that reconciles every managed workspace file a full installation
/// reset is about to make unreachable.
/// </summary>
/// <remarks>
/// It is reached only once the host-tools marker pair is provably absent and the Campaign-marker
/// receipt is terminal, and it must finish before anything may delete the Grimoire. Between those two
/// points sits the last body of durable state the database and the filesystem hold jointly: rows that
/// say a file exists, and files that no row will admit to after the database is gone.
/// </remarks>
internal interface IFullInstallationResetManagedFileReconciler
{

    Task<Result<InstallationResetActivePublication>> ReconcileAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication publication,
        SqliteConnection connection,
        CancellationToken cancellationToken);

}

/// <summary>
/// The production managed-file reconciler.
/// </summary>
/// <remarks>
/// Four published phases, each one a fact the next may rely on. <c>InventoryPrepared</c> fixes the
/// exact source vector; <c>WriteIntentsReconciled</c> means no write is still mid-flight;
/// <c>WorkItemsReconciled</c> means every adopted file has been erased or refused and fixes the
/// work-item vector routing produced; <c>TerminalInventoryVerified</c> means both inventories are
/// classified and the counts add up.
///
/// <para>Every publication advances the authenticated envelope revision and therefore stales the proof
/// and the authority bound to the one it replaced, so both are reminted after each one. That is not
/// defensive style: an effect issued under a superseded authority would be authorized by a durable
/// record that no longer exists, which is precisely the window a crash between two effects opens.</para>
///
/// <para>The reconciler never opens a connection, never resolves a path, never names a credential, and
/// never deletes anything itself. Files are removed by the one shared erasure state machine, reached
/// through two internal kernel overloads it alone may call, and by the write-intent recovery service.
/// What this type owns is the order, the authentication, and the arithmetic.</para>
/// </remarks>
internal sealed class FullInstallationResetManagedFileReconciler(
    IInstallationResetActiveStore activeStore,
    CovenantManagedFileErasureKernel kernel,
    ManagedFileWriteIntentRecoveryService writeIntentRecovery)
    : IFullInstallationResetManagedFileReconciler
{

    private readonly IInstallationResetActiveStore _activeStore =
        activeStore ?? throw new ArgumentNullException(nameof(activeStore));

    private readonly CovenantManagedFileErasureKernel _kernel =
        kernel ?? throw new ArgumentNullException(nameof(kernel));

    private readonly ManagedFileWriteIntentRecoveryService _writeIntentRecovery =
        writeIntentRecovery ?? throw new ArgumentNullException(nameof(writeIntentRecovery));

    public async Task<Result<InstallationResetActivePublication>> ReconcileAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication publication,
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(heldInstallationLock);

        ArgumentNullException.ThrowIfNull(publication);

        ArgumentNullException.ThrowIfNull(connection);

        // Asserted, never acquired and never disposed. The caller owns this lock for the whole
        // operation, and a reconciler that reacquired it would either deadlock against the owner or
        // release an installation claim the rest of the reset still relies on.
        heldInstallationLock.AssertHeldFor(_activeStore.GuardedRoot);

        Result<CovenantDigest> databaseIdentity = ReadDatabaseFileIdentity(connection);

        if (databaseIdentity.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(databaseIdentity.Error);

        }

        Result<ReconciliationState> state = await RevalidateAsync(
            heldInstallationLock,
            publication,
            connection,
            databaseIdentity.Value,
            cancellationToken).ConfigureAwait(false);

        if (state.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(state.Error);

        }

        Result<ReconciliationState> prepared = await PrepareInventoryAsync(
            heldInstallationLock,
            state.Value,
            connection,
            databaseIdentity.Value,
            cancellationToken).ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(prepared.Error);

        }

        Result<ReconciliationState> writes = await ReconcileWriteIntentsAsync(
            heldInstallationLock,
            prepared.Value,
            connection,
            databaseIdentity.Value,
            cancellationToken).ConfigureAwait(false);

        if (writes.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(writes.Error);

        }

        Result<ReconciliationState> workItems = await ReconcileWorkItemsAsync(
            heldInstallationLock,
            writes.Value,
            connection,
            databaseIdentity.Value,
            cancellationToken).ConfigureAwait(false);

        if (workItems.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(workItems.Error);

        }

        return await VerifyTerminalInventoryAsync(
            heldInstallationLock,
            workItems.Value,
            connection,
            databaseIdentity.Value,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Publishes the exact source vector, or revalidates the one an earlier attempt already published.
    /// </summary>
    /// <remarks>
    /// A resumed operation never re-derives the inventory. It reads the journal again and requires it
    /// to reproduce the published vector exactly: a missing, extra, duplicated, reordered, or changed
    /// source means the database is not the one the checkpoint was written against, and continuing
    /// would be reconciling an inventory nobody authorized.
    /// </remarks>
    private async Task<Result<ReconciliationState>> PrepareInventoryAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        ReconciliationState state,
        SqliteConnection connection,
        CovenantDigest databaseIdentity,
        CancellationToken cancellationToken)
    {

        Result<ImmutableArray<Guid>> sources = await ReadSourceVectorAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        if (sources.IsFailure)
        {

            return Result<ReconciliationState>.Failure(sources.Error);

        }

        Result<CovenantDigest> vector =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector(sources.Value);

        if (vector.IsFailure)
        {

            return Result<ReconciliationState>.Failure(vector.Error);

        }

        if (state.ManagedFile is { } existing)
        {

            return existing.SourceCount == checked((ulong)sources.Value.Length)
                && existing.SourceWriteIntentVectorDigest == vector.Value
                    ? Result<ReconciliationState>.Success(state)
                    : Inert<ReconciliationState>();

        }

        return await PublishAsync(
            heldInstallationLock,
            state,
            new FullInstallationResetManagedFileCheckpointV1(
                Version: 1,
                FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared,
                checked((ulong)sources.Value.Length),
                sources.Value,
                vector.Value,
                LocalErasureWorkItemCount: null,
                OrderedLocalErasureWorkItemIds: null,
                LocalErasureWorkItemVectorDigest: null,
                SafeTerminalWriteIntentCount: null,
                ManualWriteOrphanCount: null,
                CompletedWorkItemCount: null,
                ManualWorkItemOrphanCount: null,
                TerminalClassificationDigest: null),
            connection,
            databaseIdentity,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Drives every write intent that is still mid-flight to one of its two terminal outcomes.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction. A crash between a managed-file syscall and the compare-and-swap that
    /// records it leaves the row nonterminal, and the next attempt reads the same row and reruns the
    /// same recovery against the same two candidate leaves — the second pass observes the child already
    /// absent rather than issuing a second effect.
    /// </remarks>
    private async Task<Result<ReconciliationState>> ReconcileWriteIntentsAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        ReconciliationState state,
        SqliteConnection connection,
        CovenantDigest databaseIdentity,
        CancellationToken cancellationToken)
    {

        if (state.ManagedFile is not
            { Phase: FullInstallationResetManagedFileReconciliationPhase.InventoryPrepared } checkpoint)
        {

            return Result<ReconciliationState>.Success(state);

        }

        Result<IReadOnlyList<ManagedFileWriteIntentRow>> inventory =
            await ManagedFileWriteIntentStore.ListInventoryAsync(
                connection,
                FullInstallationResetManagedFileBounds.MaximumVectorCount,
                cancellationToken).ConfigureAwait(false);

        if (inventory.IsFailure)
        {

            return Result<ReconciliationState>.Failure(inventory.Error);

        }

        foreach (ManagedFileWriteIntentRow row in inventory.Value)
        {

            if (row.Phase is < ManagedFileWriteIntentPhase.Prepared
                or > ManagedFileWriteIntentPhase.ParentFsynced)
            {

                continue;

            }

            Result current = await state.Authority.AssertCurrentAsync(cancellationToken)
                .ConfigureAwait(false);

            if (current.IsFailure)
            {

                return Result<ReconciliationState>.Failure(current.Error);

            }

            Result<ManagedFileWriteIntentRecoveryOutcome> recovered = await _writeIntentRecovery
                .RecoverForFullInstallationResetAsync(connection, row, cancellationToken)
                .ConfigureAwait(false);

            if (recovered.IsFailure)
            {

                return Result<ReconciliationState>.Failure(recovered.Error);

            }

        }

        return await PublishAsync(
            heldInstallationLock,
            state,
            checkpoint with
            {
                Phase = FullInstallationResetManagedFileReconciliationPhase.WriteIntentsReconciled,
            },
            connection,
            databaseIdentity,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Routes every adopted source through the shared erasure kernel and resumes every unfinished work
    /// item, then fixes the work-item vector both produced.
    /// </summary>
    /// <remarks>
    /// An adopted source reuses the work item the database already authorized when one is active, and
    /// creates exactly one otherwise. That is the same reuse the live erasure path performs, reached
    /// through the same insert guard and the same partial unique index, so no second opener or delete
    /// algorithm exists and no source can acquire two active work items.
    /// </remarks>
    private async Task<Result<ReconciliationState>> ReconcileWorkItemsAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        ReconciliationState state,
        SqliteConnection connection,
        CovenantDigest databaseIdentity,
        CancellationToken cancellationToken)
    {

        if (state.ManagedFile is not
            { Phase: FullInstallationResetManagedFileReconciliationPhase.WriteIntentsReconciled } checkpoint)
        {

            return Result<ReconciliationState>.Success(state);

        }

        Result<IReadOnlyList<ManagedFileWriteIntentRow>> sources =
            await ManagedFileWriteIntentStore.ListInventoryAsync(
                connection,
                FullInstallationResetManagedFileBounds.MaximumVectorCount,
                cancellationToken).ConfigureAwait(false);

        if (sources.IsFailure)
        {

            return Result<ReconciliationState>.Failure(sources.Error);

        }

        foreach (ManagedFileWriteIntentRow row in sources.Value)
        {

            if (row.Phase is not ManagedFileWriteIntentPhase.AdoptedAndLabeled)
            {

                continue;

            }

            Result<Guid?> active = await LocalErasureWorkItemStore
                .TryReadActiveWorkItemIdForSourceAsync(connection, row.WriteOperationId, cancellationToken)
                .ConfigureAwait(false);

            if (active.IsFailure)
            {

                return Result<ReconciliationState>.Failure(active.Error);

            }

            Result<CovenantArtifactErasureProgress> reconciled = await _kernel
                .ReconcileSourceForFullInstallationResetAsync(
                    connection,
                    new CovenantManagedFileErasureRequest(
                        active.Value ?? Guid.NewGuid(),
                        state.OperationId,
                        row.WriteOperationId,
                        row.ArtifactId,
                        row.SensitivityLabelId,
                        checked((ulong)row.Revision)),
                    state.Authority,
                    cancellationToken).ConfigureAwait(false);

            if (reconciled.IsFailure)
            {

                return Result<ReconciliationState>.Failure(reconciled.Error);

            }

        }

        Result<IReadOnlyList<LocalErasureWorkItemRow>> pending = await LocalErasureWorkItemStore
            .ListNonTerminalAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (pending.IsFailure)
        {

            return Result<ReconciliationState>.Failure(pending.Error);

        }

        foreach (LocalErasureWorkItemRow item in pending.Value)
        {

            Result<CovenantArtifactErasureProgress> resumed = await _kernel
                .ResumeWorkItemForFullInstallationResetAsync(
                    connection,
                    item,
                    state.Authority,
                    cancellationToken).ConfigureAwait(false);

            if (resumed.IsFailure)
            {

                return Result<ReconciliationState>.Failure(resumed.Error);

            }

        }

        Result<ImmutableArray<Guid>> workItems = await ReadWorkItemVectorAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        if (workItems.IsFailure)
        {

            return Result<ReconciliationState>.Failure(workItems.Error);

        }

        Result<CovenantDigest> vector =
            FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector(workItems.Value);

        if (vector.IsFailure)
        {

            return Result<ReconciliationState>.Failure(vector.Error);

        }

        return await PublishAsync(
            heldInstallationLock,
            state,
            checkpoint with
            {
                Phase = FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled,
                LocalErasureWorkItemCount = checked((ulong)workItems.Value.Length),
                OrderedLocalErasureWorkItemIds = workItems.Value,
                LocalErasureWorkItemVectorDigest = vector.Value,
            },
            connection,
            databaseIdentity,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Classifies both inventories, requires the counts to add up, and publishes the terminal digest.
    /// </summary>
    /// <remarks>
    /// This is the gate the rest of the reset stands on. Every source must be safely terminal or an
    /// authenticated manual orphan, every work item completed or an authenticated manual orphan, and
    /// both sums must equal the inventories fixed earlier. A nonterminal row, a row that vanished, a
    /// row that appeared, or a row belonging to a different installation all fail here, and failing
    /// here is what refuses Grimoire deletion.
    /// </remarks>
    private async Task<Result<InstallationResetActivePublication>> VerifyTerminalInventoryAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        ReconciliationState state,
        SqliteConnection connection,
        CovenantDigest databaseIdentity,
        CancellationToken cancellationToken)
    {

        if (state.ManagedFile is not { } checkpoint)
        {

            return Inert<InstallationResetActivePublication>();

        }

        if (checkpoint.Phase
            is FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified)
        {

            return Result<InstallationResetActivePublication>.Success(state.Publication);

        }

        if (checkpoint.Phase
            is not FullInstallationResetManagedFileReconciliationPhase.WorkItemsReconciled)
        {

            return Inert<InstallationResetActivePublication>();

        }

        Result current = await state.Authority.AssertCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(current.Error);

        }

        Result<TerminalClassification> classified = await ClassifyAsync(
            connection,
            checkpoint,
            cancellationToken).ConfigureAwait(false);

        if (classified.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(classified.Error);

        }

        Result<CovenantDigest> digest =
            FullInstallationResetManagedFileDigests.TerminalClassification(
                classified.Value.Sources,
                classified.Value.WorkItems);

        if (digest.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(digest.Error);

        }

        Result<ReconciliationState> published = await PublishAsync(
            heldInstallationLock,
            state,
            checkpoint with
            {
                Phase = FullInstallationResetManagedFileReconciliationPhase.TerminalInventoryVerified,
                SafeTerminalWriteIntentCount = classified.Value.SafeTerminalWriteIntents,
                ManualWriteOrphanCount = classified.Value.ManualWriteOrphans,
                CompletedWorkItemCount = classified.Value.CompletedWorkItems,
                ManualWorkItemOrphanCount = classified.Value.ManualWorkItemOrphans,
                TerminalClassificationDigest = digest.Value,
            },
            connection,
            databaseIdentity,
            cancellationToken).ConfigureAwait(false);

        return published.IsFailure
            ? Result<InstallationResetActivePublication>.Failure(published.Error)
            : Result<InstallationResetActivePublication>.Success(published.Value.Publication);

    }

    /// <summary>
    /// Reads both inventories back and turns them into the exact terminal classification.
    /// </summary>
    private async Task<Result<TerminalClassification>> ClassifyAsync(
        SqliteConnection connection,
        FullInstallationResetManagedFileCheckpointV1 checkpoint,
        CancellationToken cancellationToken)
    {

        Result<IReadOnlyList<ManagedFileWriteIntentRow>> sources =
            await ManagedFileWriteIntentStore.ListInventoryAsync(
                connection,
                FullInstallationResetManagedFileBounds.MaximumVectorCount,
                cancellationToken).ConfigureAwait(false);

        if (sources.IsFailure)
        {

            return Result<TerminalClassification>.Failure(sources.Error);

        }

        Result<IReadOnlyList<LocalErasureWorkItemRow>> workItems = await LocalErasureWorkItemStore
            .ListInventoryAsync(
                connection,
                FullInstallationResetManagedFileBounds.MaximumVectorCount,
                cancellationToken).ConfigureAwait(false);

        if (workItems.IsFailure)
        {

            return Result<TerminalClassification>.Failure(workItems.Error);

        }

        // The inventories are the ones the checkpoint committed to, or the reconciliation is looking
        // at a database somebody changed underneath it.
        if (!SameIdentities(
                checkpoint.OrderedSourceWriteOperationIds,
                sources.Value.Select(static row => row.WriteOperationId))
            || checkpoint.OrderedLocalErasureWorkItemIds is not { } expectedWorkItems
            || !SameIdentities(
                expectedWorkItems,
                workItems.Value.Select(static row => row.WorkItemId)))
        {

            return Inert<TerminalClassification>();

        }

        // An adopted source is only an orphan the operation may account for when its own erasure work
        // item refused. Without that refusal it is simply a source nothing has finished, and no count
        // may absorb it.
        HashSet<Guid> refusedSources =
            [.. workItems.Value
                .Where(static row => row.State is LocalErasureWorkItemState.ManualBlocker)
                .Select(static row => row.SourceWriteOperationId)];

        ImmutableArray<FullInstallationResetManagedSourceClassificationV1>.Builder sourceBuilder =
            ImmutableArray.CreateBuilder<FullInstallationResetManagedSourceClassificationV1>(
                sources.Value.Count);

        ulong safeWrites = 0;

        ulong manualWrites = 0;

        foreach (ManagedFileWriteIntentRow row in sources.Value)
        {

            switch (row.Phase)
            {

                case ManagedFileWriteIntentPhase.Cleaned:
                case ManagedFileWriteIntentPhase.Erased:

                    safeWrites = checked(safeWrites + 1);

                    sourceBuilder.Add(
                        new FullInstallationResetManagedSourceClassificationV1(
                            row.WriteOperationId,
                            row.Phase,
                            BlockerEvidenceDigest: null));

                    break;

                case ManagedFileWriteIntentPhase.ManualNonrevocable:
                case ManagedFileWriteIntentPhase.AdoptedAndLabeled
                    when refusedSources.Contains(row.WriteOperationId):

                    Result<CovenantDigest> blocker =
                        FullInstallationResetManagedFileDigests.BlockerEvidence(
                            row.WriteOperationId,
                            FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan,
                            CovenantErasureBlocker.ManualOwnershipMismatch);

                    if (blocker.IsFailure)
                    {

                        return Result<TerminalClassification>.Failure(blocker.Error);

                    }

                    manualWrites = checked(manualWrites + 1);

                    sourceBuilder.Add(
                        new FullInstallationResetManagedSourceClassificationV1(
                            row.WriteOperationId,
                            row.Phase,
                            blocker.Value));

                    break;

                default:

                    // Still mid-flight or still adopted. Nothing downstream may treat this
                    // installation as accounted for.
                    return Inert<TerminalClassification>();

            }

        }

        ImmutableArray<FullInstallationResetManagedWorkItemClassificationV1>.Builder workItemBuilder =
            ImmutableArray.CreateBuilder<FullInstallationResetManagedWorkItemClassificationV1>(
                workItems.Value.Count);

        ulong completed = 0;

        ulong manualItems = 0;

        foreach (LocalErasureWorkItemRow row in workItems.Value)
        {

            switch (row.State)
            {

                case LocalErasureWorkItemState.Completed when row.DeletionEvidence is { } evidence:

                    completed = checked(completed + 1);

                    workItemBuilder.Add(
                        new FullInstallationResetManagedWorkItemClassificationV1(
                            row.WorkItemId,
                            row.State,
                            evidence,
                            BlockerEvidenceDigest: null));

                    break;

                case LocalErasureWorkItemState.ManualBlocker:

                    Result<CovenantDigest> blocker =
                        FullInstallationResetManagedFileDigests.BlockerEvidence(
                            row.WorkItemId,
                            FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan,
                            CovenantErasureBlocker.ManualOwnershipMismatch);

                    if (blocker.IsFailure)
                    {

                        return Result<TerminalClassification>.Failure(blocker.Error);

                    }

                    manualItems = checked(manualItems + 1);

                    workItemBuilder.Add(
                        new FullInstallationResetManagedWorkItemClassificationV1(
                            row.WorkItemId,
                            row.State,
                            DeletionEvidence: null,
                            blocker.Value));

                    break;

                default:

                    return Inert<TerminalClassification>();

            }

        }

        // The arithmetic the whole gate exists to state. Recomputed here from the rows themselves
        // rather than accumulated from the routing loop, so a route that silently skipped an entry
        // cannot balance its own books.
        if (checked(safeWrites + manualWrites) != checkpoint.SourceCount
            || checked(completed + manualItems) != checkpoint.LocalErasureWorkItemCount!.Value)
        {

            return Inert<TerminalClassification>();

        }

        return Result<TerminalClassification>.Success(
            new TerminalClassification(
                sourceBuilder.MoveToImmutable(),
                workItemBuilder.MoveToImmutable(),
                safeWrites,
                manualWrites,
                completed,
                manualItems));

    }

    private async Task<Result<ImmutableArray<Guid>>> ReadSourceVectorAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        Result<IReadOnlyList<ManagedFileWriteIntentRow>> inventory =
            await ManagedFileWriteIntentStore.ListInventoryAsync(
                connection,
                FullInstallationResetManagedFileBounds.MaximumVectorCount,
                cancellationToken).ConfigureAwait(false);

        return inventory.IsFailure
            ? Result<ImmutableArray<Guid>>.Failure(inventory.Error)
            : Bounded(inventory.Value.Select(static row => row.WriteOperationId));

    }

    private async Task<Result<ImmutableArray<Guid>>> ReadWorkItemVectorAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        Result<IReadOnlyList<LocalErasureWorkItemRow>> inventory = await LocalErasureWorkItemStore
            .ListInventoryAsync(
                connection,
                FullInstallationResetManagedFileBounds.MaximumVectorCount,
                cancellationToken).ConfigureAwait(false);

        return inventory.IsFailure
            ? Result<ImmutableArray<Guid>>.Failure(inventory.Error)
            : Bounded(inventory.Value.Select(static row => row.WorkItemId));

    }

    /// <summary>
    /// Refuses an inventory too large to authenticate rather than truncating it.
    /// </summary>
    private static Result<ImmutableArray<Guid>> Bounded(IEnumerable<Guid> identities)
    {

        ImmutableArray<Guid> ordered = [.. identities];

        return ordered.Length > FullInstallationResetManagedFileBounds.MaximumVectorCount
            ? Inert<ImmutableArray<Guid>>()
            : Result<ImmutableArray<Guid>>.Success(ordered);

    }

    private static bool SameIdentities(ImmutableArray<Guid> expected, IEnumerable<Guid> observed)
    {

        int index = 0;

        foreach (Guid identity in observed)
        {

            if (index >= expected.Length || expected[index] != identity)
            {

                return false;

            }

            index++;

        }

        return index == expected.Length;

    }

    /// <summary>
    /// Publishes one checkpoint and remints the proof and authority against the record it produced.
    /// </summary>
    private async Task<Result<ReconciliationState>> PublishAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        ReconciliationState state,
        FullInstallationResetManagedFileCheckpointV1 next,
        SqliteConnection connection,
        CovenantDigest databaseIdentity,
        CancellationToken cancellationToken)
    {

        Result current = await state.Authority.AssertCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<ReconciliationState>.Failure(current.Error);

        }

        InstallationResetActiveRecord record = state.Publication.Payload.ToRecord() with
        {
            HostToolsMarkerPairReset = state.MarkerCheckpoint with { ManagedFile = next },
        };

        Result<InstallationResetActivePublication> published = await _activeStore.AdvanceAsync(
            heldInstallationLock,
            state.Publication,
            record,
            cancellationToken).ConfigureAwait(false);

        if (published.IsFailure)
        {

            return Result<ReconciliationState>.Failure(published.Error);

        }

        // The authority that authorized the effects behind this publication is bound to the envelope
        // revision the publication just superseded. It is retired here rather than left reachable.
        state.Authority.Dispose();

        return await RevalidateAsync(
            heldInstallationLock,
            published.Value,
            connection,
            databaseIdentity,
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Reauthenticates the durable record and mints a fresh proof and authority against it.
    /// </summary>
    /// <remarks>
    /// Nothing is trusted from the caller except the lock. The publication handed in is compared
    /// against the one the store reads back, the marker-pair checkpoint has to be at
    /// <c>PairAbsenceVerified</c> with a terminal Campaign receipt, and the database file has to be the
    /// same physical object it was — a reconciliation whose Grimoire was swapped underneath it would be
    /// reading one installation's rows and deleting another installation's files.
    /// </remarks>
    private async Task<Result<ReconciliationState>> RevalidateAsync(
        ArcanumMaintenanceLock heldInstallationLock,
        InstallationResetActivePublication expected,
        SqliteConnection connection,
        CovenantDigest expectedDatabaseIdentity,
        CancellationToken cancellationToken)
    {

        // Reread rather than carried forward. The file this operation is reconciling rows out of has
        // to still be the same physical object it authenticated against, or it is reading one
        // installation's journal and deleting another installation's files.
        Result<CovenantDigest> databaseIdentity = ReadDatabaseFileIdentity(connection);

        if (databaseIdentity.IsFailure || databaseIdentity.Value != expectedDatabaseIdentity)
        {

            return Inert<ReconciliationState>();

        }

        Result<InstallationResetActiveRecoveryState> recovered = await _activeStore
            .RecoverAsync(heldInstallationLock, cancellationToken)
            .ConfigureAwait(false);

        if (recovered.IsFailure
            || recovered.Value.Outcome is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
            || recovered.Value.Publication is not { } publication
            || !PublicationEquals(expected, publication)
            || publication.Payload.Scope is not InstallationResetScope.All
            || publication.Payload.FullInstallationResetRemediationClaim is not { } claim
            || publication.Payload.HostToolsMarkerPairReset is not { } marker
            || marker.Phase is not HostToolsMarkerPairResetPhase.PairAbsenceVerified
            || !HasTerminalCampaignReceipt(marker)
            || marker.RestartProof.SignedAttestation.OperationId != claim.OperationId
            || marker.MarkerIntentVectorDigest is not { } campaignTerminal)
        {

            return Inert<ReconciliationState>();

        }

        AuthenticatedFullInstallationResetManagedFileJournalProof proof =
            AuthenticatedFullInstallationResetManagedFileJournalProof.Create(
                new MintTicket(),
                heldInstallationLock,
                publication,
                marker,
                claim.OperationId,
                claim.InstallationId,
                marker.OwnerEffectDigest,
                campaignTerminal,
                databaseIdentity.Value);

        return Result<ReconciliationState>.Success(
            new ReconciliationState(
                publication,
                marker,
                marker.ManagedFile,
                claim.OperationId,
                FullInstallationResetManagedFileErasureAuthority.Create(this, proof)));

    }

    /// <summary>
    /// A Campaign receipt is terminal when its deleted and orphan counts account for every intent.
    /// </summary>
    /// <remarks>
    /// A predicate over the persisted shape rather than a flag, because that is how the receipt is
    /// recorded. An inventory with nothing to delete reaches its terminal shape with both counts at
    /// zero and an intent count of zero, and the sum still has to hold.
    /// </remarks>
    private static bool HasTerminalCampaignReceipt(HostToolsMarkerPairResetCheckpointV1 marker) =>
        marker.MarkerIntentCount is { } intents
        && marker.DeletedCount is { } deleted
        && marker.OrphanCount is { } orphans
        && checked(deleted + orphans) == intents;

    private static bool PublicationEquals(
        InstallationResetActivePublication left,
        InstallationResetActivePublication right) =>
        left.EnvelopeDigest == right.EnvelopeDigest
        && left.Anchor.Revision == right.Anchor.Revision
        && left.Anchor.OperationId == right.Anchor.OperationId
        && left.Anchor.InstallationId == right.Anchor.InstallationId
        && left.Anchor.EnvelopeDigest == right.Anchor.EnvelopeDigest
        && left.Location.Digest == right.Location.Digest;

    /// <summary>
    /// Reads the physical identity of the open database file itself.
    /// </summary>
    private static Result<CovenantDigest> ReadDatabaseFileIdentity(SqliteConnection connection) =>
        !string.IsNullOrWhiteSpace(connection.DataSource)
        && FileHandleIdentityInterop.TryGetPathIdentity(
            connection.DataSource,
            out FileHandleIdentity identity)
            ? Result<CovenantDigest>.Success(ManagedFilePhysicalIdentity.Digest(identity))
            : Inert<CovenantDigest>();

    /// <summary>
    /// One content-free refusal for every way this operation can decline to continue.
    /// </summary>
    /// <remarks>
    /// Deliberately indistinguishable. The differences between "the record moved", "the inventory
    /// changed", and "the database is not the one we authenticated against" are exactly the
    /// distinctions an attacker would use to probe an installation being erased, and none of them
    /// changes what the operator has to do.
    /// </remarks>
    private static Result<T> Inert<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.RecoveryRequired,
            "The full installation reset requires recovery."));

    private sealed record ReconciliationState(
        InstallationResetActivePublication Publication,
        HostToolsMarkerPairResetCheckpointV1 MarkerCheckpoint,
        FullInstallationResetManagedFileCheckpointV1? ManagedFile,
        Guid OperationId,
        FullInstallationResetManagedFileErasureAuthority Authority);

    private sealed record TerminalClassification(
        ImmutableArray<FullInstallationResetManagedSourceClassificationV1> Sources,
        ImmutableArray<FullInstallationResetManagedWorkItemClassificationV1> WorkItems,
        ulong SafeTerminalWriteIntents,
        ulong ManualWriteOrphans,
        ulong CompletedWorkItems,
        ulong ManualWorkItemOrphans);

    /// <summary>
    /// The unforgeable token that makes the proof constructor private in practice as well as in name.
    /// </summary>
    private sealed class MintTicket
    {
    }

    /// <summary>
    /// What one managed-file reconciliation is authorized by, frozen at the moment it was authenticated.
    /// </summary>
    /// <remarks>
    /// Private to the reconciler, so nothing else in the assembly can name it, construct one, or hand
    /// one to the authority. It binds the exact held lock, the exact authenticated publication and its
    /// envelope revision and digest, the operation and installation, the owner-effect digest, the
    /// terminal Campaign-marker digest, and the physical identity of the database file it read all of
    /// that from.
    /// </remarks>
    private sealed class AuthenticatedFullInstallationResetManagedFileJournalProof
    {

        private AuthenticatedFullInstallationResetManagedFileJournalProof(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication publication,
            HostToolsMarkerPairResetCheckpointV1 markerCheckpoint,
            Guid operationId,
            Guid installationId,
            CovenantDigest ownerEffectDigest,
            CovenantDigest campaignMarkerTerminalDigest,
            CovenantDigest databaseFileIdentityDigest)
        {

            HeldInstallationLock = heldInstallationLock;

            Publication = publication;

            MarkerCheckpoint = markerCheckpoint;

            OperationId = operationId;

            InstallationId = installationId;

            OwnerEffectDigest = ownerEffectDigest;

            CampaignMarkerTerminalDigest = campaignMarkerTerminalDigest;

            DatabaseFileIdentityDigest = databaseFileIdentityDigest;

        }

        internal ArcanumMaintenanceLock HeldInstallationLock { get; }

        internal InstallationResetActivePublication Publication { get; }

        internal HostToolsMarkerPairResetCheckpointV1 MarkerCheckpoint { get; }

        internal Guid OperationId { get; }

        internal Guid InstallationId { get; }

        internal CovenantDigest OwnerEffectDigest { get; }

        internal CovenantDigest CampaignMarkerTerminalDigest { get; }

        internal CovenantDigest DatabaseFileIdentityDigest { get; }

        internal static AuthenticatedFullInstallationResetManagedFileJournalProof Create(
            MintTicket mintTicket,
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication publication,
            HostToolsMarkerPairResetCheckpointV1 markerCheckpoint,
            Guid operationId,
            Guid installationId,
            CovenantDigest ownerEffectDigest,
            CovenantDigest campaignMarkerTerminalDigest,
            CovenantDigest databaseFileIdentityDigest)
        {

            ArgumentNullException.ThrowIfNull(mintTicket);

            return new AuthenticatedFullInstallationResetManagedFileJournalProof(
                heldInstallationLock,
                publication,
                markerCheckpoint,
                operationId,
                installationId,
                ownerEffectDigest,
                campaignMarkerTerminalDigest,
                databaseFileIdentityDigest);

        }

    }

    /// <summary>
    /// The only thing that authorizes a managed-file effect during an attested full installation reset.
    /// </summary>
    /// <remarks>
    /// It retains the lock and the proof but owns neither, and exposes no lease, SQL scope, path,
    /// connection, handle, or serializable field. The single operation it offers is
    /// <see cref="AssertCurrentAsync"/>, which reauthenticates through the reconciler that minted it
    /// rather than from anything it cached — so a record that moved, an envelope revision that
    /// advanced, or a database file that changed identity all revoke it without it having to notice.
    ///
    /// <para>Disposal is one-shot through <see cref="Interlocked.Exchange{T}(ref T, T)"/> and every
    /// later assertion refuses. Each publication retires the authority bound to the revision it
    /// superseded, and a retired authority that still answered would authorize an effect against a
    /// record that no longer exists.</para>
    /// </remarks>
    internal sealed class FullInstallationResetManagedFileErasureAuthority
        : IManagedFileErasureRevalidator, IDisposable
    {

        private readonly FullInstallationResetManagedFileReconciler _owner;

        private AuthenticatedFullInstallationResetManagedFileJournalProof? _proof;

        private FullInstallationResetManagedFileErasureAuthority(
            FullInstallationResetManagedFileReconciler owner,
            AuthenticatedFullInstallationResetManagedFileJournalProof proof)
        {

            _owner = owner;

            _proof = proof;

        }

        /// <summary>
        /// The one factory, and it accepts only a proof the reconciler itself minted.
        /// </summary>
        /// <remarks>
        /// The parameter is typed <c>object</c> and pattern-matched to the reconciler's private proof
        /// type on purpose. The authority has to be nameable by the erasure kernel it authorizes, so it
        /// cannot be private — but its input can be, and a factory that named the proof in its
        /// signature would be an assembly-wide seam anything could aim a forgery at.
        /// </remarks>
        internal static FullInstallationResetManagedFileErasureAuthority Create(
            FullInstallationResetManagedFileReconciler owner,
            object proof)
        {

            ArgumentNullException.ThrowIfNull(owner);

            return proof is AuthenticatedFullInstallationResetManagedFileJournalProof typedProof
                ? new FullInstallationResetManagedFileErasureAuthority(owner, typedProof)
                : throw new InvalidOperationException(
                    "The full-installation reset managed-file erasure authority is unavailable.");

        }

        public async ValueTask<Result> AssertCurrentAsync(CancellationToken cancellationToken)
        {

            if (Volatile.Read(ref _proof) is not { } proof)
            {

                return Inert();

            }

            Result<InstallationResetActiveRecoveryState> recovered = await _owner._activeStore
                .RecoverAsync(proof.HeldInstallationLock, cancellationToken)
                .ConfigureAwait(false);

            if (recovered.IsFailure
                || recovered.Value.Outcome
                    is not InstallationResetActiveRecoveryOutcome.AuthenticatedV2
                || recovered.Value.Publication is not { } current
                || !PublicationEquals(proof.Publication, current)
                || current.Payload.FullInstallationResetRemediationClaim is not { } claim
                || claim.OperationId != proof.OperationId
                || claim.InstallationId != proof.InstallationId
                || current.Payload.HostToolsMarkerPairReset is not { } marker
                || marker.Phase is not HostToolsMarkerPairResetPhase.PairAbsenceVerified
                || !HasTerminalCampaignReceipt(marker)
                || marker.OwnerEffectDigest != proof.OwnerEffectDigest
                || marker.MarkerIntentVectorDigest != proof.CampaignMarkerTerminalDigest)
            {

                return Inert();

            }

            return Result.Success();

        }

        public void Dispose() =>
            Interlocked.Exchange(ref _proof, null);

        private static Result Inert() =>
            Result.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The full installation reset requires recovery."));

    }

}
