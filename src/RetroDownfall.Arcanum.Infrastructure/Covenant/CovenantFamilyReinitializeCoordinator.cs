using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The one owner of every durable transition a Covenant family reinitialize performs.
/// </summary>
/// <remarks>
/// A seam, and exactly one per operation kind, because these are the steps that cannot be undone.
/// Keeping them behind one interface means the coordinator can be exercised against every crash point
/// without a real database being destroyed, and means there is a single place to look for "what does
/// reinitialize actually do to the file" (§10.17).
///
/// <para>Every method is idempotent by phase. Recovery re-enters at the phase the checkpoint records
/// and calls the same methods again, so a step that had already committed must observe that and
/// return rather than repeat.</para>
/// </remarks>
internal interface ICovenantFamilyReinitializeTransition
{

    /// <summary>Quiesces the disclosure writer and drains the central connection owner.</summary>
    Task<Result> CloseHandlesAsync(CancellationToken cancellationToken);

    /// <summary>Securely drops every closed-manifest Covenant-prefixed object.</summary>
    Task<Result> DropFamilyAsync(CancellationToken cancellationToken);

    /// <summary>Compacts or export-replaces the database file and reports its new identity.</summary>
    Task<Result<CovenantDigest>> CompactAsync(CancellationToken cancellationToken);

    /// <summary>Installs the canonical and accelerator tiers fresh, and reports the new generation.</summary>
    Task<Result<Guid>> InstallTiersAsync(CancellationToken cancellationToken);

    /// <summary>Truncates the WAL and proves the sidecars are gone.</summary>
    Task<Result> TruncateAndVerifySidecarsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reopens read-only under the unpublished candidate key and verifies both tiers, without
    /// creating WAL or SHM, then closes that handle.
    /// </summary>
    Task<Result> VerifyReopenAsync(CancellationToken cancellationToken);

    /// <summary>Publishes the committed key, issuer, availability, and gate transition.</summary>
    Task<Result> PublishCommittedAsync(
        ICovenantExclusiveOperationLease lease,
        Guid newDatasetGeneration,
        CancellationToken cancellationToken);

    /// <summary>Reopens the disclosure writer after publication succeeded.</summary>
    Task<Result> ReopenDisclosureWriterAsync(CancellationToken cancellationToken);

}

/// <summary>
/// One bounded page of protected state a reinitialize must erase before it drops anything.
/// </summary>
internal sealed record CovenantReinitializeErasureWork(
    IReadOnlyList<CovenantProtectedArtifactErasurePage> DatabasePages,
    IReadOnlyList<CovenantManagedFileErasureRequest> ManagedFiles);

/// <summary>
/// Supplies the protected state a reinitialize is responsible for erasing.
/// </summary>
internal interface ICovenantReinitializeErasureSource
{

    Task<Result<CovenantReinitializeErasureWork>> EnumerateAsync(
        Guid operationId,
        Guid datasetGeneration,
        CancellationToken cancellationToken);

}

/// <summary>
/// The single coordinator for the Covenant family reinitialize operation.
/// </summary>
/// <remarks>
/// It owns the phase machine, the checkpoint, the exclusive recovery owner, and the one disposition.
/// It owns no deletion algorithm: database artifacts go through the shared protected-artifact kernel
/// and managed files through the shared managed-file kernel, both borrowing this coordinator's own
/// exclusive lease rather than acquiring one, so no kernel can reopen admission or deadlock against
/// this operation's drain.
///
/// <para>The gate owner always uses the durable server operation identity.
/// <c>RequestedOperationId</c> is the caller's replay key and never a gate owner: two different
/// callers replaying the same name must resolve to the same operation, and an owner built from the
/// replay key would let the second one adopt the first one's closed scope.</para>
/// </remarks>
internal sealed class CovenantFamilyReinitializeCoordinator(
    CovenantRequestedOperationStarter starter,
    ILongRunningOperationCoordinator operations,
    ILongRunningOperationStore store,
    ICovenantOperationGate gate,
    ICovenantProtectedArtifactErasureKernel artifacts,
    ICovenantManagedFileErasureKernel managedFiles,
    ICovenantReinitializeErasureSource erasureSource,
    ICovenantFamilyReinitializeTransition transition,
    TimeProvider timeProvider)
{

    /// <summary>How long the apply request owns its ledger lease.</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);

    private const string Summary = "Reinitializing the Covenant schema family.";

    private readonly CovenantRequestedOperationStarter _starter =
        starter ?? throw new ArgumentNullException(nameof(starter));

    private readonly ILongRunningOperationCoordinator _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    private readonly ILongRunningOperationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly ICovenantOperationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly ICovenantProtectedArtifactErasureKernel _artifacts =
        artifacts ?? throw new ArgumentNullException(nameof(artifacts));

    private readonly ICovenantManagedFileErasureKernel _managedFiles =
        managedFiles ?? throw new ArgumentNullException(nameof(managedFiles));

    private readonly ICovenantReinitializeErasureSource _erasureSource =
        erasureSource ?? throw new ArgumentNullException(nameof(erasureSource));

    private readonly ICovenantFamilyReinitializeTransition _transition =
        transition ?? throw new ArgumentNullException(nameof(transition));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Starts, or replays, the caller-named reinitialize operation.
    /// </summary>
    /// <remarks>
    /// Replay is resolved before any token is decoded, which is what lets an expired token still find
    /// the operation it already admitted instead of being treated as a fresh authority grant.
    /// </remarks>
    internal async Task<Result<LongRunningOperationRequestIdentityResult>> StartAsync(
        Guid requestedOperationId,
        CovenantDigest applyRequestDigest,
        CovenantDigest effectDigest,
        string ownerId,
        CancellationToken cancellationToken) =>
        await _starter.StartRequestedAsync(
            LongRunningOperationKinds.CovenantFamilyReinitialize,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            Summary,
            _time.GetUtcNow(),
            requestedOperationId,
            applyRequestDigest,
            effectDigest,
            ownerId,
            LeaseDuration,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs, or resumes, the reinitialize and reports how it left admission.
    /// </summary>
    internal async Task<Result<CovenantExclusiveLeaseDisposition>> RunAsync(
        LongRunningOperation operation,
        CovenantFamilyReinitializeCheckpointV1 checkpoint,
        string ownerId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentNullException.ThrowIfNull(checkpoint);

        CovenantExclusiveRecoveryOwner owner = new(
            operation.Id,
            CovenantExclusiveOperation.CovenantFamilyReinitialize,
            new CovenantDigest(Convert.FromHexString(checkpoint.EffectDigest)));

        Result<CovenantExclusiveLease> acquired = checkpoint.Phase == CovenantFamilyReinitializePhase.Planned
            ? await _gate.AcquireExclusiveAsync(owner, cancellationToken).ConfigureAwait(false)
            : await _gate.ResumeExclusiveAsync(owner, cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<CovenantExclusiveLeaseDisposition>.Failure(acquired.Error);

        }

        await using CovenantExclusiveLease lease = acquired.Value;

        Result<CovenantArtifactErasureAuthority> authority = CovenantArtifactErasureAuthority.ForExclusive(
            lease,
            CovenantExclusiveOperation.CovenantFamilyReinitialize);

        if (authority.IsFailure)
        {

            return Result<CovenantExclusiveLeaseDisposition>.Failure(authority.Error);

        }

        CovenantFamilyReinitializeCheckpointV1 state = checkpoint;

        bool durablyMutated = state.OldFamilyDropped || state.CanonicalInstalled || state.AcceleratorInstalled;

        try
        {

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.AdmissionClosed,
                static (_, _) => Task.FromResult(Result.Success()),
                cancellationToken).ConfigureAwait(false);

            Result erased = await EraseProtectedStateAsync(operation, state, authority.Value, cancellationToken)
                .ConfigureAwait(false);

            if (erased.IsFailure)
            {

                // A manual file blocker leaves the family intact and admission closed. Dropping the
                // family now would destroy the database's own record of a file it still cannot prove
                // it removed from disk.
                return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, cancellationToken)
                    .ConfigureAwait(false);

            }

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.LocalArtifactsProcessed,
                static (_, _) => Task.FromResult(Result.Success()),
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.HandlesClosed,
                (_, token) => _transition.CloseHandlesAsync(token),
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.FamilyDropped,
                (_, token) => _transition.DropFamilyAsync(token),
                cancellationToken).ConfigureAwait(false);

            durablyMutated = true;

            state = state with { OldFamilyDropped = true };

            // Compaction and tier installation carry a value out as well as a phase, so they advance
            // through the same guard rather than beside it. Advancing beside it is how a resumed run
            // rewrites its own phase downward and repeats an effect it already committed.
            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.DatabaseCompacted,
                async (current, token) =>
                {

                    Result<CovenantDigest> compacted = await _transition
                        .CompactAsync(token)
                        .ConfigureAwait(false);

                    return compacted.IsFailure
                        ? Result<CovenantFamilyReinitializeCheckpointV1>.Failure(compacted.Error)
                        : Result<CovenantFamilyReinitializeCheckpointV1>.Success(
                            current with { CompactedFileIdentityDigest = compacted.Value.ToString() });

                },
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.AcceleratorInstalled,
                async (current, token) =>
                {

                    Result<Guid> installed = await _transition
                        .InstallTiersAsync(token)
                        .ConfigureAwait(false);

                    return installed.IsFailure
                        ? Result<CovenantFamilyReinitializeCheckpointV1>.Failure(installed.Error)
                        : Result<CovenantFamilyReinitializeCheckpointV1>.Success(
                            current with
                            {
                                CanonicalInstalled = true,
                                AcceleratorInstalled = true,
                                NewDatasetGeneration = installed.Value,
                            });

                },
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.SidecarsVerified,
                (_, token) => _transition.TruncateAndVerifySidecarsAsync(token),
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantFamilyReinitializePhase.ReopenedVerified,
                (_, token) => _transition.VerifyReopenAsync(token),
                cancellationToken).ConfigureAwait(false);

        }
        catch (CovenantReinitializeStepFailedException)
        {

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, cancellationToken)
                .ConfigureAwait(false);

        }

        // A checkpoint past the install phase without the generation that install produced is durable
        // state disagreeing with itself. There is nothing to publish and no safe guess, so admission
        // stays closed and the operation stays resumable.
        if (state.NewDatasetGeneration is not { } publishedGeneration)
        {

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, cancellationToken)
                .ConfigureAwait(false);

        }

        // Publication happens while the gate is still held. Reopening first would leave a window in
        // which new work could be authorized under keys this transition just invalidated.
        Result published = await _transition
            .PublishCommittedAsync(lease, publishedGeneration, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, cancellationToken)
                .ConfigureAwait(false);

        }

        Result writer = await _transition.ReopenDisclosureWriterAsync(cancellationToken).ConfigureAwait(false);

        if (writer.IsFailure)
        {

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, cancellationToken)
                .ConfigureAwait(false);

        }

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: durablyMutated,
                HealthPublished: true));

        return await CloseAsync(lease, disposition, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Erases every database-owned protected artifact and managed file the operation is responsible
    /// for, before anything is dropped.
    /// </summary>
    /// <remarks>
    /// Both kernels receive this coordinator's own live lease wrapped as an erasure authority. Neither
    /// acquires, completes, or disposes a lease of its own, which is what keeps an exclusive caller
    /// from deadlocking against its own drain by trying to take an ordinary lease.
    /// </remarks>
    private async Task<Result> EraseProtectedStateAsync(
        LongRunningOperation operation,
        CovenantFamilyReinitializeCheckpointV1 checkpoint,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        Result<CovenantReinitializeErasureWork> work = await _erasureSource
            .EnumerateAsync(operation.Id, checkpoint.OldDatasetGeneration, cancellationToken)
            .ConfigureAwait(false);

        if (work.IsFailure)
        {

            return Result.Failure(work.Error);

        }

        foreach (CovenantProtectedArtifactErasurePage page in work.Value.DatabasePages)
        {

            Result<CovenantArtifactErasureProgress> erased = await _artifacts
                .ErasePageAsync(page, authority, cancellationToken)
                .ConfigureAwait(false);

            if (erased.IsFailure || erased.Value.IsBlocked)
            {

                return Result.Failure(
                    erased.IsFailure
                        ? erased.Error
                        : new Error(
                            ErrorCodes.Covenant.ManualArtifactErasureRequired,
                            "A protected artifact could not be erased before the family drop."));

            }

        }

        foreach (CovenantManagedFileErasureRequest file in work.Value.ManagedFiles)
        {

            Result<CovenantArtifactErasureProgress> erased = await _managedFiles
                .EraseAsync(file, authority, cancellationToken)
                .ConfigureAwait(false);

            if (erased.IsFailure || erased.Value.IsBlocked)
            {

                return Result.Failure(
                    erased.IsFailure
                        ? erased.Error
                        : new Error(
                            ErrorCodes.Covenant.ManualArtifactErasureRequired,
                            "A managed workspace file could not be erased before the family drop."));

            }

        }

        return Result.Success();

    }

    private Task<CovenantFamilyReinitializeCheckpointV1> AdvanceAsync(
        LongRunningOperation operation,
        CovenantFamilyReinitializeCheckpointV1 checkpoint,
        string ownerId,
        CovenantFamilyReinitializePhase phase,
        Func<CovenantFamilyReinitializeCheckpointV1, CancellationToken, Task<Result>> step,
        CancellationToken cancellationToken) =>
        AdvanceAsync(
            operation,
            checkpoint,
            ownerId,
            phase,
            async (current, token) =>
            {

                Result performed = await step(current, token).ConfigureAwait(false);

                return performed.IsFailure
                    ? Result<CovenantFamilyReinitializeCheckpointV1>.Failure(performed.Error)
                    : Result<CovenantFamilyReinitializeCheckpointV1>.Success(current);

            },
            cancellationToken);

    /// <summary>
    /// Performs one phase's step exactly once and records it, or returns the checkpoint untouched.
    /// </summary>
    /// <remarks>
    /// The guard is the whole resume contract: a phase already recorded is a step already committed,
    /// and re-running it would repeat an effect the database cannot roll back. Every step goes through
    /// here, including the two that carry a value out, because a step advanced beside the guard can
    /// rewrite its own phase downward and make a resumed run repeat work it already finished.
    /// </remarks>
    private async Task<CovenantFamilyReinitializeCheckpointV1> AdvanceAsync(
        LongRunningOperation operation,
        CovenantFamilyReinitializeCheckpointV1 checkpoint,
        string ownerId,
        CovenantFamilyReinitializePhase phase,
        Func<CovenantFamilyReinitializeCheckpointV1, CancellationToken, Task<Result<CovenantFamilyReinitializeCheckpointV1>>> step,
        CancellationToken cancellationToken)
    {

        if (checkpoint.Phase >= phase)
        {

            return checkpoint;

        }

        Result<CovenantFamilyReinitializeCheckpointV1> performed =
            await step(checkpoint, cancellationToken).ConfigureAwait(false);

        if (performed.IsFailure)
        {

            throw new CovenantReinitializeStepFailedException(performed.Error);

        }

        return await CheckpointAsync(
            operation,
            performed.Value with { Phase = phase },
            ownerId,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<CovenantFamilyReinitializeCheckpointV1> CheckpointAsync(
        LongRunningOperation operation,
        CovenantFamilyReinitializeCheckpointV1 checkpoint,
        string ownerId,
        CancellationToken cancellationToken)
    {

        LongRunningOperation? current = await _store.GetAsync(operation.Id, cancellationToken).ConfigureAwait(false);

        bool saved = await _operations.CheckpointAsync(
            operation.Id,
            ownerId,
            current?.CheckpointVersion ?? operation.CheckpointVersion,
            CovenantFamilyReinitializeCheckpointV1.CurrentVersion,
            CovenantRecoveryCheckpointCodec.Encode(checkpoint),
            checkpointReference: null,
            Summary,
            cancellationToken).ConfigureAwait(false);

        if (!saved)
        {

            throw new CovenantReinitializeStepFailedException(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "The Covenant reinitialize checkpoint was written by another owner."));

        }

        return checkpoint;

    }

    private static async Task<Result<CovenantExclusiveLeaseDisposition>> CloseAsync(
        CovenantExclusiveLease lease,
        CovenantExclusiveLeaseDisposition disposition,
        CancellationToken cancellationToken)
    {

        Result closed = await lease.CompleteAsync(disposition, cancellationToken).ConfigureAwait(false);

        return closed.IsFailure
            ? Result<CovenantExclusiveLeaseDisposition>.Failure(closed.Error)
            : Result<CovenantExclusiveLeaseDisposition>.Success(disposition);

    }

}

/// <summary>
/// The internal signal that one reinitialize step did not complete.
/// </summary>
/// <remarks>
/// An exception rather than a returned result, so a step failure cannot be accidentally ignored by a
/// caller that forgot to check. It never escapes the coordinator: every catch selects
/// <c>KeepClosed</c>, which is the only safe answer when a durable step's outcome is unknown.
/// </remarks>
internal sealed class CovenantReinitializeStepFailedException(Error error)
    : Exception(error.Message)
{

    internal Error Error { get; } = error;

}

/// <summary>
/// The one registered recovery handler for an interrupted family reinitialize.
/// </summary>
/// <remarks>
/// It reconstructs the exact exclusive recovery owner from immutable checkpoint fields and hands the
/// operation back as reconciliation work. It deliberately does not resume inline: the resume needs the
/// live transition owner, the erasure kernels, and a held installation lock, and a readiness pass that
/// carried all three would be a second coordinator.
/// </remarks>
internal sealed class CovenantFamilyReinitializeRecoveryHandler : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.CovenantFamilyReinitialize;

    public int SupportedCheckpointVersion => CovenantFamilyReinitializeCheckpointV1.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        if (operation.CheckpointPayload is not { } payload)
        {

            // No checkpoint means admission was never closed under this operation's owner, so nothing
            // durable can be outstanding and the row may close.
            return Task.FromResult(
                LongRunningOperationRecoveryResult.Abandoned(CovenantReinitializeNeverStarted));

        }

        Result<CovenantFamilyReinitializeCheckpointV1> checkpoint =
            CovenantRecoveryCheckpointCodec.DecodeFamilyReinitialize(payload);

        if (checkpoint.IsFailure)
        {

            return Task.FromResult(
                LongRunningOperationRecoveryResult.RequiresAttention(
                    LongRunningOperationErrorCodes.CorruptCheckpoint));

        }

        return Task.FromResult(
            checkpoint.Value.Phase == CovenantFamilyReinitializePhase.ReopenedVerified
                ? LongRunningOperationRecoveryResult.Completed()
                : LongRunningOperationRecoveryResult.RequiresAttention(CovenantReinitializeResumeRequired));

    }

    /// <summary>The terminal code for a reinitialize that never closed admission.</summary>
    internal const string CovenantReinitializeNeverStarted = "covenant.family_reinitialize_never_started";

    /// <summary>The parked code for a reinitialize whose durable phase must be resumed.</summary>
    internal const string CovenantReinitializeResumeRequired = "covenant.family_reinitialize_resume_required";

}
