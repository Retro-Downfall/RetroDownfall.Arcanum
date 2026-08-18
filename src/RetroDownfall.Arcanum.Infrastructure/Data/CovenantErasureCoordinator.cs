using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The one owner of every durable storage transition a Covenant erasure performs.
/// </summary>
/// <remarks>
/// A seam, for the same reason <c>ICovenantFamilyReinitializeTransition</c> is one: these are the
/// steps that cannot be undone, and keeping them behind one interface means the phase machine can be
/// exercised against every crash point without a real database being destroyed. It also means there
/// is a single place to look for "what does a reset actually do to the file" (§10.20.4).
///
/// <para>Every method is idempotent by phase. Recovery re-enters at the phase the checkpoint records
/// and calls the same methods again, so a step that had already committed must observe that and
/// return rather than repeat. The coordinator's guard skips whole recorded phases, but a crash
/// between a step's external effect and its checkpoint save leaves the phase unrecorded — so the
/// step itself has to be safe to run twice.</para>
///
/// <para>Nothing here takes, completes, or disposes a lease. The coordinator holds exactly one, and a
/// storage owner that could acquire a second would be able to reopen admission underneath the
/// operation that closed it.</para>
/// </remarks>
internal interface ICovenantErasureTransition
{

    /// <summary>
    /// Erases the canonical family in one exclusive initialized secure-delete transaction and reports
    /// the generation of the single new dataset it created.
    /// </summary>
    /// <remarks>
    /// The operation code selects what is preserved rather than what is deleted: a healthy-catalog
    /// factory erasure keeps schema objects, <c>grimoire_feature_schemas</c>, authority taint, and
    /// nonrevocable disclosure evidence that an ordinary reset also keeps, and additionally reseeds
    /// the canonical and accelerator singletons. The two arms do the same thing to storage and differ
    /// only in what survives, which is why one method takes both.
    /// </remarks>
    Task<Result<Guid>> ApplyCanonicalErasureAsync(
        CovenantExclusiveOperation operation,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the candidate dataset generation a previous pass already committed.
    /// </summary>
    /// <remarks>
    /// Neither durable reset checkpoint has a field for it — <c>DataRetentionMutationCheckpointV3</c>
    /// and <c>DataRetentionFactoryResetCheckpointV1</c> were both frozen fixed-width by #118 — so a
    /// resumed run asks the database rather than a checkpoint. That is the better source anyway: the
    /// dataset row is the commit authority for its own identity, and a checkpoint field would be a
    /// second copy that could only ever disagree in the case that matters.
    /// </remarks>
    Task<Result<Guid>> ReadCandidateDatasetGenerationAsync(CancellationToken cancellationToken);

    /// <summary>Clears every pool and drains direct handles through the central connection owner.</summary>
    Task<Result> CloseHandlesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs a checked <c>wal_checkpoint(TRUNCATE)</c>, refusing on a busy flag or a remaining frame.
    /// </summary>
    /// <remarks>
    /// Called twice — once before compaction and once after the accelerator is installed — because
    /// each of those steps can leave frames of its own. Checked rather than best-effort: the shutdown
    /// checkpointer discards its result, which is correct for shutdown and useless as a proof that
    /// erased pages are actually gone.
    /// </remarks>
    Task<Result> TruncateWalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inventories sidecars and staging artifacts, then compacts, using a verified SQLCipher
    /// export-and-atomic-replace when <c>VACUUM</c> alone cannot prove the freed pages are gone.
    /// </summary>
    Task<Result> CompactAsync(CancellationToken cancellationToken);

    /// <summary>Installs the empty accelerator and runs rank-1 integrity over it.</summary>
    Task<Result> InitializeAcceleratorAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clears pools and handles a final time and proves the absence of every sidecar, journal, temp,
    /// staging, and replaced file.
    /// </summary>
    Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reopens read-only on the unpublished candidate state, on a handle that cannot create WAL or
    /// SHM, verifies both tiers, and closes that handle.
    /// </summary>
    Task<Result> VerifyReopenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the committed dataset, master, authority, and capability snapshot while the caller's
    /// exclusive gate is still held.
    /// </summary>
    /// <remarks>
    /// It borrows the lease rather than owning it. Publication has to happen inside the closure or
    /// there is a window in which new work could be authorized under keys this erasure just
    /// invalidated.
    /// </remarks>
    Task<Result> PublishCommittedAsync(
        ICovenantExclusiveOperationLease lease,
        Guid candidateDatasetGeneration,
        CancellationToken cancellationToken);

}

/// <summary>
/// Everything one erasure is responsible for removing, plus the one disclosure fact it must report.
/// </summary>
/// <remarks>
/// The disclosure flag travels with the work rather than being recomputed later, because it describes
/// the same inventory the pages were derived from. Reading it separately afterwards would let a
/// completed erasure report a disclosure posture belonging to a family that no longer exists.
/// </remarks>
internal sealed record CovenantErasureWork(
    IReadOnlyList<CovenantProtectedArtifactErasurePage> DatabasePages,
    IReadOnlyList<CovenantManagedFileErasureRequest> ManagedFiles,
    bool ExternalDisclosuresNotRevocable);

/// <summary>
/// Supplies the bounded, comprehensive protected state one erasure must remove.
/// </summary>
internal interface ICovenantErasureInventorySource
{

    Task<Result<CovenantErasureWork>> EnumerateAsync(
        Guid operationId,
        CovenantExclusiveOperation operation,
        Guid datasetGeneration,
        CancellationToken cancellationToken);

}

/// <summary>
/// The immutable facts a durable erasure checkpoint carries, whichever shape recorded them.
/// </summary>
/// <remarks>
/// A Covenant reset writes <c>DataRetentionMutationCheckpointV3</c> and a healthy-catalog factory
/// erasure writes <c>DataRetentionFactoryResetCheckpointV1</c>. The two shapes differ because their
/// journal headers do, but the four facts an erasure resumes from are identical, and one projection
/// is what lets a single coordinator own both without a second phase machine (§10.20.4).
/// </remarks>
internal sealed record CovenantErasureCheckpointState(
    Guid OperationId,
    CovenantExclusiveOperation Operation,
    CovenantDigest EffectDigest,
    CovenantResetPhase Phase)
{

    /// <summary>
    /// The exact gate identity this checkpoint describes, and the only one it may be resumed under.
    /// </summary>
    public CovenantExclusiveRecoveryOwner Owner => new(OperationId, Operation, EffectDigest);

    /// <summary>
    /// Projects the Covenant arm of a version-3 retention mutation journal.
    /// </summary>
    /// <remarks>
    /// <paramref name="operationId"/> is the durable server operation the row belongs to, and the
    /// projection refuses a payload naming anything else. That check is the whole reason this lives in
    /// one place: a retry with a changed plan must not be able to rebuild an owner matching a closed
    /// scope it has no right to adopt, and a rule enforced at two call sites is a rule that eventually
    /// holds at one.
    ///
    /// <para><paramref name="describesCovenantErasure"/> separates the two failures that must not be
    /// reported as one. A version-3 row carrying no Covenant arm is an ordinary retention mutation
    /// that closed nothing, and its remedy is ordinary reconciliation; a row that carries an arm this
    /// build cannot resume has admission closed behind it, and its remedy is an operator. Collapsing
    /// them would tell somebody to leave a stuck ordinary mutation alone forever.</para>
    /// </remarks>
    public static Result<CovenantErasureCheckpointState> FromMutationCheckpoint(
        Guid operationId,
        ReadOnlySpan<byte> payload,
        out bool describesCovenantErasure)
    {

        // Fail closed: unknown counts as an erasure. A payload that would not decode cannot say
        // whether it closed admission, and treating it as an ordinary mutation would reconcile a row
        // that may have a half-erased family and a shut gate behind it.
        describesCovenantErasure = true;

        Result<DataRetentionMutationCheckpointV3> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionMutation(payload);

        if (decoded.IsFailure)
        {

            return Result<CovenantErasureCheckpointState>.Failure(decoded.Error);

        }

        // A version-3 row with no arm describes a mutation that closed nothing. There is no exclusive
        // scope to adopt, and inventing one would close a scope this operation never opened.
        if (decoded.Value.Covenant is not { } arm)
        {

            describesCovenantErasure = false;

            return Unresumable();

        }

        Result<CovenantExclusiveRecoveryOwner> owner = CovenantRecoveryCheckpointCodec.RecoveryOwner(arm);

        return owner.IsFailure
            ? Unresumable()
            : Project(operationId, owner.Value, arm.Phase);

    }

    /// <summary>
    /// Projects a version-1 healthy-catalog factory erasure journal.
    /// </summary>
    public static Result<CovenantErasureCheckpointState> FromFactoryResetCheckpoint(
        Guid operationId,
        ReadOnlySpan<byte> payload)
    {

        Result<DataRetentionFactoryResetCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryReset(payload);

        if (decoded.IsFailure)
        {

            return Result<CovenantErasureCheckpointState>.Failure(decoded.Error);

        }

        Result<CovenantExclusiveRecoveryOwner> owner =
            CovenantRecoveryCheckpointCodec.RecoveryOwner(decoded.Value);

        return owner.IsFailure
            ? Unresumable()
            : Project(operationId, owner.Value, decoded.Value.Phase);

    }

    private static Result<CovenantErasureCheckpointState> Project(
        Guid operationId,
        CovenantExclusiveRecoveryOwner owner,
        CovenantResetPhase phase) =>
        owner.OperationId == operationId && CovenantResetPhaseMachine.IsDeclared(phase)
            ? Result<CovenantErasureCheckpointState>.Success(
                new CovenantErasureCheckpointState(owner.OperationId, owner.Operation, owner.EffectDigest, phase))
            : Unresumable();

    private static Result<CovenantErasureCheckpointState> Unresumable() =>
        Result<CovenantErasureCheckpointState>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This Covenant erasure checkpoint cannot be resumed by this build."));

}

/// <summary>
/// How one erasure finished, in facts an operator surface can report independently.
/// </summary>
/// <remarks>
/// Three separate booleans rather than one status code, because they answer three different questions
/// and an operator needs all three. <see cref="CanonicalResetApplied"/> says the family's rows are
/// gone. <see cref="LocalSecureErasureComplete"/> says the bytes were proven unrecoverable, which is
/// strictly later and can fail on its own. <see cref="ExternalDisclosuresNotRevocable"/> says nothing
/// about local work at all: it is what this installation cannot undo no matter how completely it
/// erases itself. Collapsing them would let a half-proven erasure read as a finished one.
/// </remarks>
internal sealed record CovenantErasureCompletion(
    CovenantExclusiveLeaseDisposition Disposition,
    bool CanonicalResetApplied,
    bool LocalSecureErasureComplete,
    bool ExternalDisclosuresNotRevocable);

/// <summary>
/// The single coordinator for Covenant reset and healthy-catalog factory erasure.
/// </summary>
/// <remarks>
/// It owns the phase machine, the exclusive recovery owner, the two shared kernels' authority, and
/// the one disposition. It owns no deletion algorithm: database artifacts go through the shared
/// protected-artifact kernel and managed files through the shared managed-file kernel, both borrowing
/// this coordinator's own exclusive lease rather than acquiring one, so no kernel can reopen admission
/// or deadlock against this operation's drain. It never resolves a managed-file capability opener or
/// ownership verifier, because a second opinion about which file is Arcanum's is one opinion too many
/// (§10.17).
///
/// <para>The gate owner always uses the durable server operation identity. An optional caller
/// <c>RequestedOperationId</c> is the normalized replay key and never a gate owner: two callers
/// replaying the same name must resolve to the same operation, and an owner built from the replay key
/// would let the second adopt the first's closed scope.</para>
///
/// <para>The phase order puts the whole database half before the first managed file is touched. The
/// managed-file kernel persists its durable work item before its first external effect, and that work
/// item is a database row — so a file deleted before the transaction that authorized it would be a
/// deletion no surviving row can explain.</para>
/// </remarks>
internal sealed class CovenantErasureCoordinator(
    ILongRunningOperationCoordinator operations,
    ILongRunningOperationStore store,
    ICovenantOperationGate gate,
    ICovenantProtectedArtifactErasureKernel artifacts,
    ICovenantManagedFileErasureKernel managedFiles,
    ICovenantErasureInventorySource inventory,
    ICovenantErasureTransition transition,
    ICovenantDisclosureWriterLifecycle disclosureWriter,
    ILogger<CovenantErasureCoordinator> logger)
{

    /// <summary>
    /// How long the one reopening decision may take, on a token this coordinator owns.
    /// </summary>
    /// <remarks>
    /// The caller's token is an HTTP request token. After a durable mutation it is exactly the wrong
    /// token to decide admission with: a client that hung up would leave the installation closed with
    /// a proven-complete erasure behind it, and the gate's disposition throws on a cancelled token
    /// rather than quietly proceeding.
    /// </remarks>
    internal static readonly TimeSpan DispositionBound = TimeSpan.FromSeconds(30);

    private const string ResetSummary = "Erasing the Covenant family.";

    private readonly ILongRunningOperationCoordinator _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    private readonly ILongRunningOperationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly ICovenantOperationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly ICovenantProtectedArtifactErasureKernel _artifacts =
        artifacts ?? throw new ArgumentNullException(nameof(artifacts));

    private readonly ICovenantManagedFileErasureKernel _managedFiles =
        managedFiles ?? throw new ArgumentNullException(nameof(managedFiles));

    private readonly ICovenantErasureInventorySource _inventory =
        inventory ?? throw new ArgumentNullException(nameof(inventory));

    private readonly ICovenantErasureTransition _transition =
        transition ?? throw new ArgumentNullException(nameof(transition));

    private readonly ICovenantDisclosureWriterLifecycle _disclosureWriter =
        disclosureWriter ?? throw new ArgumentNullException(nameof(disclosureWriter));

    private readonly ILogger<CovenantErasureCoordinator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs, or resumes, one erasure and reports how it left admission.
    /// </summary>
    /// <remarks>
    /// The checkpoint must already record <c>InventoryPrepared</c> — <c>CovenantResetCheckpointInitiator</c>
    /// is the only thing that can commit it, and its <c>GateAdmission</c> cannot be constructed without
    /// that commit winning. This method therefore never opens a closure that nothing durable describes.
    /// </remarks>
    internal async Task<Result<CovenantErasureCompletion>> RunAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        Guid datasetGeneration,
        string ownerId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentNullException.ThrowIfNull(checkpoint);

        Result admissible = RequireResumableCheckpoint(operation, checkpoint);

        if (admissible.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(admissible.Error);

        }

        CovenantExclusiveRecoveryOwner owner = new(
            checkpoint.OperationId,
            checkpoint.Operation,
            checkpoint.EffectDigest);

        // Acquire closes a scope for the first time; resume adopts one this operation already closed
        // and never reopened. Using the wrong verb is not a style choice: acquiring an already-closed
        // scope is refused, and resuming a scope nobody closed has nothing to adopt.
        Result<CovenantExclusiveLease> acquired = checkpoint.Phase == CovenantResetPhaseMachine.First
            ? await _gate.AcquireExclusiveAsync(owner, cancellationToken).ConfigureAwait(false)
            : await _gate.ResumeExclusiveAsync(owner, cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(acquired.Error);

        }

        await using CovenantExclusiveLease lease = acquired.Value;

        Result<CovenantArtifactErasureAuthority> authority = CovenantArtifactErasureAuthority.ForExclusive(
            lease,
            checkpoint.Operation);

        if (authority.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(authority.Error);

        }

        return await RunUnderLeaseAsync(
            operation,
            checkpoint,
            datasetGeneration,
            ownerId,
            lease,
            authority.Value,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<CovenantErasureCompletion>> RunUnderLeaseAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        Guid datasetGeneration,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken)
    {

        CovenantErasureCheckpointState state = checkpoint;

        ErasureProgress progress = new(state.Phase);

        try
        {

            // Quiescing is not a phase. It is idempotent, it writes nothing, and it has to happen
            // before the first artifact is touched: the disclosure writer is the one component still
            // able to append after admission closed, and a receipt appended over a row this erasure is
            // deleting would outlive the thing it describes.
            Result quiesced = await _disclosureWriter.QuiesceAsync(cancellationToken).ConfigureAwait(false);

            if (quiesced.IsFailure)
            {

                return await AbortBeforeErasureAsync(lease, progress, quiesced.Error).ConfigureAwait(false);

            }

            Result<CovenantErasureWork> work = await _inventory
                .EnumerateAsync(operation.Id, state.Operation, datasetGeneration, cancellationToken)
                .ConfigureAwait(false);

            if (work.IsFailure)
            {

                return await AbortBeforeErasureAsync(lease, progress, work.Error).ConfigureAwait(false);

            }

            progress.ExternalDisclosuresNotRevocable = work.Value.ExternalDisclosuresNotRevocable;

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.CanonicalApplied,
                async (_, token) =>
                {

                    Result erased = await EraseDatabaseArtifactsAsync(
                        work.Value,
                        authority,
                        progress,
                        token).ConfigureAwait(false);

                    if (erased.IsFailure)
                    {

                        return Result<Guid?>.Failure(erased.Error);

                    }

                    Result<Guid> applied = await _transition
                        .ApplyCanonicalErasureAsync(state.Operation, token)
                        .ConfigureAwait(false);

                    return applied.IsFailure
                        ? Result<Guid?>.Failure(applied.Error)
                        : Result<Guid?>.Success(applied.Value);

                },
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.ManagedArtifactsProcessed,
                (_, token) => EraseManagedFilesAsync(work.Value, authority, progress, token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.HandlesClosed,
                (_, token) => _transition.CloseHandlesAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.WalTruncated,
                (_, token) => _transition.TruncateWalAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.DatabaseCompacted,
                (_, token) => _transition.CompactAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.AcceleratorInitialized,
                (_, token) => _transition.InitializeAcceleratorAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.FinalWalTruncated,
                (_, token) => _transition.TruncateWalAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.SidecarsVerified,
                (_, token) => _transition.VerifySidecarAbsenceAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.ReopenedVerified,
                (_, token) => _transition.VerifyReopenAsync(token),
                progress,
                cancellationToken).ConfigureAwait(false);

        }
        catch (CovenantErasureStepFailedException failed)
        {

            _logger.LogWarning(
                "A Covenant erasure stopped at phase {ResetPhase} for durable operation {OperationId} "
                + "with {ErrorCode}; admission stays closed.",
                state.Phase,
                operation.Id,
                failed.Error.Code);

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, progress)
                .ConfigureAwait(false);

        }

        // The erasure is proven. Everything below can still fail, and none of it can make the erasure
        // untrue — which is exactly why the storage fact and the publication fact are reported
        // separately rather than folded into one outcome.
        progress.LocalSecureErasureComplete = true;

        Result<Guid> candidate = await ResolveCandidateGenerationAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (candidate.IsFailure)
        {

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, progress)
                .ConfigureAwait(false);

        }

        // Publication happens while the gate is still held. Reopening first would leave a window in
        // which new work could be authorized under keys this erasure just invalidated.
        Result published = await _transition
            .PublishCommittedAsync(lease, candidate.Value, cancellationToken)
            .ConfigureAwait(false);

        if (published.IsFailure)
        {

            _logger.LogWarning(
                "The Covenant authority transition for durable operation {OperationId} did not publish; "
                + "old contexts stay unusable and admission stays closed.",
                operation.Id);

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, progress)
                .ConfigureAwait(false);

        }

        // The warm writer may only come back against the authority that was just published. A restart
        // failure lands here, before the one disposition, so it selects KeepClosed rather than
        // reversing an erasure the storage proof already earned.
        Result writer = await _disclosureWriter.ReopenAsync(cancellationToken).ConfigureAwait(false);

        if (writer.IsFailure)
        {

            return await CloseAsync(lease, CovenantExclusiveLeaseDisposition.KeepClosed, progress)
                .ConfigureAwait(false);

        }

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: progress.DurablyMutated,
                HealthPublished: true));

        return await CloseAsync(lease, disposition, progress).ConfigureAwait(false);

    }

    /// <summary>
    /// The one shape of failure that may reopen: nothing was attempted, so nothing can be half done.
    /// </summary>
    /// <remarks>
    /// A quiesce or inventory failure happens before the first kernel call, so storage is provably
    /// untouched and <see cref="CovenantExclusiveDisposition.Select"/> answers
    /// <c>RollbackAndReopen</c> on its own. Everything from the first erased artifact onwards is a
    /// durable mutation, and the same function then answers <c>KeepClosed</c> — which is why the
    /// decision is made by the shared evidence function rather than picked by hand here.
    /// </remarks>
    private async Task<Result<CovenantErasureCompletion>> AbortBeforeErasureAsync(
        CovenantExclusiveLease lease,
        ErasureProgress progress,
        Error error)
    {

        _logger.LogWarning(
            "A Covenant erasure aborted before any artifact was touched with {ErrorCode}; admission reopens.",
            error.Code);

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: progress.DurablyMutated,
                HealthPublished: false));

        return await CloseAsync(lease, disposition, progress).ConfigureAwait(false);

    }

    /// <summary>
    /// Resolves the generation this erasure's new dataset carries, without ever inventing one.
    /// </summary>
    /// <remarks>
    /// A first pass already holds the value its own canonical erasure returned. A resumed pass does
    /// not, because neither frozen checkpoint shape has a field for it, so it reads the committed
    /// dataset row back. One source per pass: two would let a resume publish a generation that
    /// disagreed with the one on disk.
    /// </remarks>
    private async Task<Result<Guid>> ResolveCandidateGenerationAsync(
        ErasureProgress progress,
        CancellationToken cancellationToken)
    {

        if (progress.CandidateDatasetGeneration is { } known)
        {

            return Result<Guid>.Success(known);

        }

        Result<Guid> read = await _transition
            .ReadCandidateDatasetGenerationAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsSuccess && read.Value == Guid.Empty)
        {

            // Durable state disagreeing with itself: a checkpoint past the canonical phase without the
            // dataset that phase creates. There is nothing safe to publish and no safe guess.
            return Result<Guid>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "This Covenant erasure recorded its canonical phase without a candidate dataset."));

        }

        return read;

    }

    private async Task<Result> EraseDatabaseArtifactsAsync(
        CovenantErasureWork work,
        CovenantArtifactErasureAuthority authority,
        ErasureProgress progress,
        CancellationToken cancellationToken)
    {

        foreach (CovenantProtectedArtifactErasurePage page in work.DatabasePages)
        {

            Result<CovenantArtifactErasureProgress> erased = await _artifacts
                .ErasePageAsync(page, authority, cancellationToken)
                .ConfigureAwait(false);

            if (erased.IsFailure)
            {

                return Result.Failure(erased.Error);

            }

            if (erased.Value.ErasedCount > 0)
            {

                progress.DurablyMutated = true;

            }

            if (erased.Value.IsBlocked)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.ManualArtifactErasureRequired,
                        "A protected artifact could not be erased, so the canonical erasure did not run."));

            }

        }

        return Result.Success();

    }

    private async Task<Result> EraseManagedFilesAsync(
        CovenantErasureWork work,
        CovenantArtifactErasureAuthority authority,
        ErasureProgress progress,
        CancellationToken cancellationToken)
    {

        foreach (CovenantManagedFileErasureRequest file in work.ManagedFiles)
        {

            Result<CovenantArtifactErasureProgress> erased = await _managedFiles
                .EraseAsync(file, authority, cancellationToken)
                .ConfigureAwait(false);

            if (erased.IsFailure)
            {

                return Result.Failure(erased.Error);

            }

            if (erased.Value.ErasedCount > 0)
            {

                progress.DurablyMutated = true;

            }

            if (erased.Value.IsBlocked)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.ManualArtifactErasureRequired,
                        "A managed workspace file could not be erased, so local erasure is incomplete."));

            }

        }

        return Result.Success();

    }

    private Task<CovenantErasureCheckpointState> AdvanceAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CovenantResetPhase phase,
        Func<CovenantErasureCheckpointState, CancellationToken, Task<Result>> step,
        ErasureProgress progress,
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
                    ? Result<Guid?>.Failure(performed.Error)
                    : Result<Guid?>.Success(null);

            },
            progress,
            cancellationToken);

    /// <summary>
    /// Performs one phase's step exactly once and records it, or returns the checkpoint untouched.
    /// </summary>
    /// <remarks>
    /// The guard is the whole resume contract: a phase already recorded is a step already committed,
    /// and re-running it would repeat an effect the filesystem cannot roll back. The ordering itself
    /// is checked by <see cref="CovenantResetPhaseMachine.RequireAdvance"/> rather than by a
    /// comparison written here, because "is this the next phase" has three silent wrong answers and
    /// the phase machine is the only reader that knows all of them.
    /// </remarks>
    private async Task<CovenantErasureCheckpointState> AdvanceAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CovenantResetPhase phase,
        Func<CovenantErasureCheckpointState, CancellationToken, Task<Result<Guid?>>> step,
        ErasureProgress progress,
        CancellationToken cancellationToken)
    {

        if (checkpoint.Phase >= phase)
        {

            return checkpoint;

        }

        Result ordered = CovenantResetPhaseMachine.RequireAdvance(checkpoint.Phase, phase);

        if (ordered.IsFailure)
        {

            throw new CovenantErasureStepFailedException(ordered.Error);

        }

        Result<Guid?> performed = await step(checkpoint, cancellationToken).ConfigureAwait(false);

        if (performed.IsFailure)
        {

            throw new CovenantErasureStepFailedException(performed.Error);

        }

        if (performed.Value is { } generation)
        {

            progress.CandidateDatasetGeneration = generation;

            progress.CanonicalResetApplied = true;

            progress.DurablyMutated = true;

        }

        CovenantErasureCheckpointState advanced = checkpoint with { Phase = phase };

        await CheckpointAsync(operation, advanced, ownerId, cancellationToken).ConfigureAwait(false);

        return advanced;

    }

    private async Task CheckpointAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CancellationToken cancellationToken)
    {

        LongRunningOperation? current = await _store
            .GetAsync(operation.Id, cancellationToken)
            .ConfigureAwait(false);

        (int version, byte[] payload) = Encode(checkpoint);

        bool saved = await _operations.CheckpointAsync(
            operation.Id,
            ownerId,
            current?.CheckpointVersion ?? operation.CheckpointVersion,
            version,
            payload,
            CovenantResetCheckpointInitiator.CheckpointReference(operation.Kind, operation.Id),
            operation.PublicSummary ?? ResetSummary,
            cancellationToken).ConfigureAwait(false);

        if (!saved)
        {

            throw new CovenantErasureStepFailedException(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "The Covenant erasure checkpoint was written by another owner."));

        }

    }

    /// <summary>
    /// Writes the phase back into whichever durable shape this operation started under.
    /// </summary>
    /// <remarks>
    /// A reset resumes from the retention mutation journal and a factory erasure from its own, so the
    /// shape is selected by the operation code the checkpoint already carries rather than by anything
    /// this pass decides. Writing the other shape would produce a payload the matching recovery
    /// handler cannot decode.
    /// </remarks>
    private static (int Version, byte[] Payload) Encode(CovenantErasureCheckpointState checkpoint) =>
        checkpoint.Operation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure
            ? (DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                CovenantRecoveryCheckpointCodec.Encode(
                    new DataRetentionFactoryResetCheckpointV1(
                        DataRetentionFactoryResetCheckpointV1.CurrentVersion,
                        checkpoint.OperationId,
                        CovenantRecoveryCheckpointCodec.EncodeEffectDigest(checkpoint.EffectDigest),
                        checkpoint.Operation,
                        checkpoint.Phase)))
            : (DataRetentionMutationCheckpointV3.CurrentVersion,
                CovenantRecoveryCheckpointCodec.Encode(
                    new DataRetentionMutationCheckpointV3(
                        DataRetentionMutationCheckpointV3.CurrentVersion,
                        Subtype: "reset-memory",
                        Target: ((int)MemoryResetScope.Covenant).ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        new CovenantResetEffectArmV1(
                            checkpoint.OperationId,
                            CovenantRecoveryCheckpointCodec.EncodeEffectDigest(checkpoint.EffectDigest),
                            checkpoint.Operation,
                            checkpoint.Phase))));

    /// <summary>
    /// Refuses a checkpoint this build cannot resume, before any scope is closed.
    /// </summary>
    /// <remarks>
    /// The owner identity check is the one that matters: a checkpoint naming a different operation
    /// would mint an owner for a closed scope this operation has no right to adopt. It is checked
    /// before the gate rather than after, because by then the scope is already closed.
    /// </remarks>
    private static Result RequireResumableCheckpoint(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint)
    {

        if (checkpoint.Operation is not CovenantExclusiveOperation.CovenantReset
            and not CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "Only a Covenant reset or a healthy-catalog factory erasure enters the erasure coordinator.");

        }

        if (checkpoint.OperationId == Guid.Empty || checkpoint.OperationId != operation.Id)
        {

            return new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "This Covenant erasure checkpoint names a different durable operation.");

        }

        return CovenantResetPhaseMachine.RequireDeclared(checkpoint.Phase);

    }

    /// <summary>
    /// Takes the one reopening decision, on a token this coordinator owns.
    /// </summary>
    /// <remarks>
    /// A failed disposition is reported as itself and never followed by a second one. The gate is
    /// already closed at that point, so a retry with <c>KeepClosed</c> would change nothing an
    /// operator can observe while replacing the real failure with the lease's own
    /// <c>LifecycleConflict</c> — hiding the reason the reset could not reopen.
    /// </remarks>
    private async Task<Result<CovenantErasureCompletion>> CloseAsync(
        CovenantExclusiveLease lease,
        CovenantExclusiveLeaseDisposition disposition,
        ErasureProgress progress)
    {

        using CancellationTokenSource lifecycle = new(DispositionBound);

        Result closed = await lease.CompleteAsync(disposition, lifecycle.Token).ConfigureAwait(false);

        if (closed.IsFailure)
        {

            _logger.LogError(
                "A Covenant erasure could not record its {Disposition} disposition ({ErrorCode}); admission "
                + "stays closed and the operation stays adoptable.",
                disposition,
                closed.Error.Code);

            return Result<CovenantErasureCompletion>.Failure(closed.Error);

        }

        return Result<CovenantErasureCompletion>.Success(
            new CovenantErasureCompletion(
                disposition,
                progress.CanonicalResetApplied,
                progress.LocalSecureErasureComplete,
                progress.ExternalDisclosuresNotRevocable));

    }

    /// <summary>
    /// The mutable facts one run accumulates, kept out of the durable checkpoint on purpose.
    /// </summary>
    /// <remarks>
    /// None of these belong in a checkpoint: two are derived from the phase already recorded there,
    /// and the third is a dataset identity the database itself owns. A checkpoint that carried them
    /// would be a second source for facts that already have one.
    /// </remarks>
    private sealed class ErasureProgress(CovenantResetPhase resumedFrom)
    {

        /// <summary>Whether a previous pass already committed the canonical erasure.</summary>
        internal bool CanonicalResetApplied { get; set; } =
            resumedFrom >= CovenantResetPhase.CanonicalApplied;

        internal bool LocalSecureErasureComplete { get; set; }

        internal bool ExternalDisclosuresNotRevocable { get; set; }

        /// <summary>
        /// Whether anything irreversible has happened yet, which is the only fact that separates a
        /// reopening abort from one that must keep admission closed.
        /// </summary>
        internal bool DurablyMutated { get; set; } = resumedFrom > CovenantResetPhaseMachine.First;

        internal Guid? CandidateDatasetGeneration { get; set; }

    }

}

/// <summary>
/// The internal signal that one erasure step did not complete.
/// </summary>
/// <remarks>
/// An exception rather than a returned result, so a step failure cannot be accidentally ignored by a
/// caller that forgot to check. It never escapes the coordinator: every catch selects
/// <c>KeepClosed</c>, which is the only safe answer when a durable step's outcome is unknown.
/// </remarks>
internal sealed class CovenantErasureStepFailedException(Error error)
    : Exception(error.Message)
{

    internal Error Error { get; } = error;

}
