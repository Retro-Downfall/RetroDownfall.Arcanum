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
    Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(CancellationToken cancellationToken);

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
        CovenantVerifiedCandidateState candidate,
        CancellationToken cancellationToken);

}

/// <summary>The checked fold of nonrevocable disclosure buckets preserved across local erasure.</summary>
internal sealed record CovenantDisclosureExposure
{

    internal CovenantDisclosureExposure(
        long possibleAttempts,
        CovenantDisclosureCountKind countKind)
    {

        if (possibleAttempts < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(possibleAttempts));

        }

        if (countKind is not CovenantDisclosureCountKind.Exact
            and not CovenantDisclosureCountKind.LowerBound)
        {

            throw new ArgumentOutOfRangeException(nameof(countKind));

        }

        PossibleAttempts = possibleAttempts;

        CountKind = countKind;

    }

    internal long PossibleAttempts { get; }

    internal CovenantDisclosureCountKind CountKind { get; }

}

/// <summary>Effect-free facts retained from the complete pre-canonical inventory pass.</summary>
internal sealed record CovenantErasureInventorySummary
{

    internal CovenantErasureInventorySummary(
        long databaseArtifactCount,
        long managedFileArtifactCount,
        CovenantDisclosureExposure exposure)
    {

        ArgumentNullException.ThrowIfNull(exposure);

        if (databaseArtifactCount < 0 || managedFileArtifactCount < 0)
        {

            throw new ArgumentOutOfRangeException(nameof(databaseArtifactCount));

        }

        DatabaseArtifactCount = databaseArtifactCount;

        ManagedFileArtifactCount = managedFileArtifactCount;

        Exposure = exposure;

    }

    internal long DatabaseArtifactCount { get; }

    internal long ManagedFileArtifactCount { get; }

    internal CovenantDisclosureExposure Exposure { get; }

}

/// <summary>One raw-label keyset step and at most one database-kernel page.</summary>
internal sealed record CovenantDatabaseErasureBatch
{

    internal CovenantDatabaseErasureBatch(
        Guid? nextCursor,
        bool isComplete,
        CovenantProtectedArtifactErasurePage? page)
    {

        if (nextCursor == Guid.Empty || (!isComplete && nextCursor is null))
        {

            throw new ArgumentException("An incomplete database erasure batch requires a nonempty cursor.");

        }

        NextCursor = nextCursor;

        IsComplete = isComplete;

        Page = page;

    }

    internal Guid? NextCursor { get; }

    internal bool IsComplete { get; }

    internal CovenantProtectedArtifactErasurePage? Page { get; }

}

/// <summary>One raw-label keyset step and at most 256 managed-file kernel requests.</summary>
internal sealed record CovenantManagedFileErasureBatch
{

    private readonly CovenantManagedFileErasureRequest[] _requests;

    internal CovenantManagedFileErasureBatch(
        Guid? nextCursor,
        bool isComplete,
        IReadOnlyList<CovenantManagedFileErasureRequest> requests)
    {

        ArgumentNullException.ThrowIfNull(requests);

        if (nextCursor == Guid.Empty || (!isComplete && nextCursor is null))
        {

            throw new ArgumentException("An incomplete managed erasure batch requires a nonempty cursor.");

        }

        if (requests.Count > CovenantProtectedArtifactErasurePage.MaxItems)
        {

            throw new ArgumentOutOfRangeException(nameof(requests));

        }

        NextCursor = nextCursor;

        IsComplete = isComplete;

        _requests = [.. requests];

    }

    internal Guid? NextCursor { get; }

    internal bool IsComplete { get; }

    internal IReadOnlyList<CovenantManagedFileErasureRequest> Requests => _requests;

}

/// <summary>
/// Supplies the bounded, comprehensive protected state one erasure must remove.
/// </summary>
internal interface ICovenantErasureInventorySource
{

    Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
        CovenantExclusiveOperation operation,
        Guid datasetGeneration,
        CancellationToken cancellationToken);

    Task<Result> PreflightRemainingManagedAsync(CancellationToken cancellationToken);

    Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
        Guid datasetGeneration,
        Guid? afterLabelId,
        CancellationToken cancellationToken);

    Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
        Guid operationId,
        Guid? afterLabelId,
        CancellationToken cancellationToken);

    Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
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
    CovenantDisclosureExposure Exposure,
    string? BlockingErrorCode)
{

    /// <summary>The legacy projection, derived only from irreversible disclosure evidence.</summary>
    internal bool ExternalDisclosuresNotRevocable => Exposure.PossibleAttempts > 0;

}

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
    TimeProvider timeProvider,
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

    /// <summary>
    /// How long the post-proof checkpoint, publication, and warm-writer restart may take.
    /// </summary>
    internal static readonly TimeSpan PublicationAndWriterBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long an unchanged-generation writer restoration may take before rollback is refused.
    /// </summary>
    internal static readonly TimeSpan WriterRestorationBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the durable ledger gets to record an uncertain disposition independently.
    /// </summary>
    internal static readonly TimeSpan FailureRecordingBound = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan FailureRecordingRetryDelay = TimeSpan.FromMilliseconds(10);

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

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        string ownerId,
        CancellationToken cancellationToken) =>
        await RunAsync(
            operation,
            checkpoint,
            ownerId,
            factoryContinuation: null,
            CovenantExclusiveOperation.CovenantReset,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs, or resumes, a healthy-catalog factory erasure with its required ordinary cleanup.
    /// </summary>
    /// <remarks>
    /// The continuation is restart-idempotent and deliberately is not a new phase. It runs after
    /// <c>ManagedArtifactsProcessed</c> is durable and before <c>HandlesClosed</c>; therefore recovery
    /// repeats it while the former is the durable boundary and skips it once the latter is durable.
    /// </remarks>
    internal async Task<Result<CovenantErasureCompletion>> RunAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        Func<CancellationToken, Task<Result>> factoryContinuation,
        CancellationToken cancellationToken) =>
        await RunAsync(
            operation,
            checkpoint,
            ownerId,
            factoryContinuation,
            CovenantExclusiveOperation.HealthyCatalogFactoryErasure,
            cancellationToken).ConfigureAwait(false);

    private async Task<Result<CovenantErasureCompletion>> RunAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        Func<CancellationToken, Task<Result>>? factoryContinuation,
        CovenantExclusiveOperation requiredOperation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentNullException.ThrowIfNull(checkpoint);

        if (checkpoint.Operation != requiredOperation
            || requiredOperation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure
                && factoryContinuation is null)
        {

            return Result<CovenantErasureCompletion>.Failure(
                new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    requiredOperation == CovenantExclusiveOperation.CovenantReset
                        ? "The reset erasure entry point accepts only a Covenant reset."
                        : "A healthy-catalog factory erasure requires its ordinary cleanup continuation."));

        }

        Result admissible = RequireResumableCheckpoint(operation, checkpoint);

        if (admissible.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(admissible.Error);

        }

        CovenantExclusiveRecoveryOwner owner = new(
            checkpoint.OperationId,
            checkpoint.Operation,
            checkpoint.EffectDigest);

        // InventoryPrepared may be entering for the first time or resuming a drained gate.
        // ReopenedVerified may likewise follow a successful reopen whose post-disposition journal
        // finalizer lost its CAS. Every destructive intermediate phase is resume-only: acquiring an
        // open scope there would repeat effects whose checkpoint says admission must still be closed.
        Result<CovenantExclusiveLease> acquired = checkpoint.Phase is CovenantResetPhase.InventoryPrepared
            or CovenantResetPhase.ReopenedVerified
            ? await _gate.ResumeOrAcquireExclusiveAsync(owner, cancellationToken).ConfigureAwait(false)
            : await _gate.ResumeExclusiveAsync(owner, cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return Result<CovenantErasureCompletion>.Failure(acquired.Error);

        }

        await using CovenantExclusiveLease lease = acquired.Value;

        Guid? datasetGeneration = lease.Snapshot.DatasetGeneration;

        if (datasetGeneration is null || datasetGeneration == Guid.Empty)
        {

            return Result<CovenantErasureCompletion>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The exclusive Covenant lease did not capture a dataset generation."));

        }

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
            datasetGeneration.Value,
            ownerId,
            lease,
            authority.Value,
            factoryContinuation,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<Result<CovenantErasureCompletion>> RunUnderLeaseAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        Guid datasetGeneration,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantArtifactErasureAuthority authority,
        Func<CancellationToken, Task<Result>>? factoryContinuation,
        CancellationToken cancellationToken)
    {

        CovenantErasureCheckpointState state = checkpoint;

        ErasureProgress progress = new(state.Phase);

        CovenantVerifiedCandidateState candidate;

        bool resumedAtCanonical = checkpoint.Phase == CovenantResetPhase.CanonicalApplied;

        try
        {

            // Quiescing is not a phase. It is idempotent, it writes nothing, and it has to happen
            // before the first artifact is touched: the disclosure writer is the one component still
            // able to append after admission closed, and a receipt appended over a row this erasure is
            // deleting would outlive the thing it describes.
            Result quiesced = await _disclosureWriter.QuiesceAsync(cancellationToken).ConfigureAwait(false);

            if (quiesced.IsFailure)
            {

                return await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    quiesced.Error).ConfigureAwait(false);

            }

            if (state.Phase == CovenantResetPhase.InventoryPrepared)
            {

                Result<CovenantErasureInventorySummary> inventory = await _inventory
                    .PreflightBeforeCanonicalAsync(state.Operation, datasetGeneration, cancellationToken)
                    .ConfigureAwait(false);

                if (inventory.IsFailure)
                {

                    return await AbortBeforeErasureAsync(
                        operation,
                        state,
                        ownerId,
                        lease,
                        progress,
                        inventory.Error).ConfigureAwait(false);

                }

                progress.Exposure = inventory.Value.Exposure;

            }
            else
            {

                Result<CovenantDisclosureExposure> exposure = await _inventory
                    .ReadDisclosureExposureAsync(cancellationToken)
                    .ConfigureAwait(false);

                if (exposure.IsFailure)
                {

                    throw new CovenantErasureStepFailedException(exposure.Error);

                }

                progress.Exposure = exposure.Value;

                if (resumedAtCanonical)
                {

                    Result managedPreflight = await _inventory
                        .PreflightRemainingManagedAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (managedPreflight.IsFailure)
                    {

                        throw new CovenantErasureStepFailedException(managedPreflight.Error);

                    }

                }

            }

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.CanonicalApplied,
                async (_, token) =>
                {

                    Result erased = await EraseDatabaseArtifactsAsync(
                        datasetGeneration,
                        authority,
                        progress,
                        token).ConfigureAwait(false);

                    if (erased.IsFailure)
                    {

                        return Result.Failure(erased.Error);

                    }

                    progress.EffectAttempted = true;

                    Result<Guid> applied = await _transition
                        .ApplyCanonicalErasureAsync(state.Operation, token)
                        .ConfigureAwait(false);

                    return applied.IsFailure ? Result.Failure(applied.Error) : Result.Success();

                },
                progress,
                cancellationToken).ConfigureAwait(false);

            state = await AdvanceAsync(
                operation,
                state,
                ownerId,
                CovenantResetPhase.ManagedArtifactsProcessed,
                (_, token) => EraseManagedFilesAsync(
                    operation.Id,
                    authority,
                    progress,
                    token),
                progress,
                cancellationToken).ConfigureAwait(false);

            if (state.Phase < CovenantResetPhase.HandlesClosed
                && factoryContinuation is not null)
            {

                Result continued = await factoryContinuation(cancellationToken).ConfigureAwait(false);

                if (continued.IsFailure)
                {

                    throw new CovenantErasureStepFailedException(continued.Error);

                }

            }

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

            Result<CovenantVerifiedCandidateState> verified =
                await _transition.VerifyReopenAsync(cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure)
            {

                throw new CovenantErasureStepFailedException(verified.Error);

            }

            candidate = verified.Value;

        }
        catch (CovenantErasureStepFailedException failed)
        {

            _logger.LogWarning(
                "A Covenant erasure stopped at phase {ResetPhase} for durable operation {OperationId} "
                + "with {ErrorCode}; admission stays closed.",
                state.Phase,
                operation.Id,
                failed.Error.Code);

            return progress.EffectAttempted || progress.DurablyMutated
                ? await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    failed.Error.Code)
                    .ConfigureAwait(false)
                : await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    failed.Error).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            Error interrupted = MaintenanceFailure();

            return progress.EffectAttempted || progress.DurablyMutated
                ? await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    interrupted.Code)
                    .ConfigureAwait(false)
                : await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    interrupted).ConfigureAwait(false);

        }
        catch (DataRetentionLeaseLostException)
        {

            throw;

        }
        catch (Exception)
        {

            Error interrupted = MaintenanceFailure();

            return progress.EffectAttempted || progress.DurablyMutated
                ? await CloseAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    CovenantExclusiveLeaseDisposition.KeepClosed,
                    progress,
                    interrupted.Code)
                    .ConfigureAwait(false)
                : await AbortBeforeErasureAsync(
                    operation,
                    state,
                    ownerId,
                    lease,
                    progress,
                    interrupted).ConfigureAwait(false);

        }

        // The caller no longer owns cancellation after the immutable proof succeeds. The checkpoint,
        // publication, and writer restart share one bounded lifecycle token because all three must
        // finish before the separately bounded disposition decides whether admission reopens.
        using CancellationTokenSource publicationAndWriter =
            new(PublicationAndWriterBound, _timeProvider);

        try
        {

            // ReopenedVerified records that a proof succeeded, not the proof object itself. A fresh
            // pass checkpoints only after obtaining the value it will publish. A resumed pass has no
            // value to recover from its checkpoint, so it repeats the immutable verification without
            // rewriting a phase that was already durable.
            if (state.Phase < CovenantResetPhase.ReopenedVerified)
            {

                Result ordered = CovenantResetPhaseMachine.RequireAdvance(
                    state.Phase,
                    CovenantResetPhase.ReopenedVerified);

                if (ordered.IsFailure)
                {

                    throw new CovenantErasureStepFailedException(ordered.Error);

                }

                CovenantErasureCheckpointState advanced = state with
                {

                    Phase = CovenantResetPhase.ReopenedVerified,

                };

                await CheckpointAsync(operation, advanced, ownerId, publicationAndWriter.Token)
                    .ConfigureAwait(false);

                state = advanced;

            }

        }
        catch (CovenantErasureStepFailedException failed)
        {

            _logger.LogWarning(
                "A Covenant erasure stopped at phase {ResetPhase} for durable operation {OperationId} "
                + "with {ErrorCode}; admission stays closed.",
                state.Phase,
                operation.Id,
                failed.Error.Code);

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                failed.Error.Code)
                .ConfigureAwait(false);

        }
        catch (Exception)
        {

            Error interrupted = MaintenanceFailure();

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                interrupted.Code)
                .ConfigureAwait(false);

        }

        // The erasure is proven. Everything below can still fail, and none of it can make the erasure
        // untrue — which is exactly why the storage fact and the publication fact are reported
        // separately rather than folded into one outcome.
        progress.LocalSecureErasureComplete = true;

        // Publication happens while the gate is still held. Reopening first would leave a window in
        // which new work could be authorized under keys this erasure just invalidated.

        Result published = await RunLifecycleAsync(
            token => _transition.PublishCommittedAsync(lease, candidate, token),
            publicationAndWriter.Token).ConfigureAwait(false);

        if (published.IsFailure)
        {

            _logger.LogWarning(
                "The Covenant authority transition for durable operation {OperationId} did not publish; "
                + "old contexts stay unusable and admission stays closed.",
                operation.Id);

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                published.Error.Code)
                .ConfigureAwait(false);

        }

        // The warm writer may only come back against the authority that was just published. A restart
        // failure lands here, before the one disposition, so it selects KeepClosed rather than
        // reversing an erasure the storage proof already earned.
        Result writer = await RunLifecycleAsync(
            token => _disclosureWriter.ReopenAsync(token).AsTask(),
            publicationAndWriter.Token).ConfigureAwait(false);

        if (writer.IsFailure)
        {

            return await CloseAsync(
                operation,
                state,
                ownerId,
                lease,
                CovenantExclusiveLeaseDisposition.KeepClosed,
                progress,
                writer.Error.Code)
                .ConfigureAwait(false);

        }

        CovenantExclusiveLeaseDisposition disposition = CovenantExclusiveDisposition.Select(
            new CovenantExclusiveDispositionEvidence(
                StorageVerified: true,
                AuthorityVerified: true,
                DurablyMutated: progress.DurablyMutated,
                HealthPublished: true));

        return await CloseAsync(
            operation,
            state,
            ownerId,
            lease,
            disposition,
            progress,
            blockingErrorCode: null).ConfigureAwait(false);

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
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CovenantExclusiveLease lease,
        ErasureProgress progress,
        Error error)
    {

        _logger.LogWarning(
            "A Covenant erasure aborted before any artifact was touched with {ErrorCode}; admission reopens.",
            error.Code);

        using CancellationTokenSource restoration = new(WriterRestorationBound, _timeProvider);

        Result restored;

        try
        {

            restored = await _disclosureWriter
                .ReopenAsync(restoration.Token)
                .ConfigureAwait(false);

        }
        catch (Exception)
        {

            restored = Result.Failure(MaintenanceFailure());

        }

        CovenantExclusiveLeaseDisposition disposition = restored.IsSuccess
            ? CovenantExclusiveDisposition.Select(
                new CovenantExclusiveDispositionEvidence(
                    StorageVerified: true,
                    AuthorityVerified: true,
                    DurablyMutated: progress.DurablyMutated,
                    HealthPublished: false))
            : CovenantExclusiveLeaseDisposition.KeepClosed;

        return await CloseAsync(
            operation,
            checkpoint,
            ownerId,
            lease,
            disposition,
            progress,
            restored.IsSuccess ? error.Code : ErrorCodes.Covenant.MaintenanceFailed)
            .ConfigureAwait(false);

    }

    private async Task<Result> EraseDatabaseArtifactsAsync(
        Guid datasetGeneration,
        CovenantArtifactErasureAuthority authority,
        ErasureProgress progress,
        CancellationToken cancellationToken)
    {

        Guid? cursor = null;

        while (true)
        {

            Result<CovenantDatabaseErasureBatch> batch = await _inventory
                .ReadNextDatabaseBatchAsync(datasetGeneration, cursor, cancellationToken)
                .ConfigureAwait(false);

            if (batch.IsFailure)
            {

                return Result.Failure(batch.Error);

            }

            if (!batch.Value.IsComplete && batch.Value.NextCursor == cursor)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A bounded Covenant database inventory page did not advance its cursor."));

            }

            if (batch.Value.Page is { } page)
            {

                progress.EffectAttempted = true;

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

            if (batch.Value.IsComplete)
            {

                return Result.Success();

            }

            cursor = batch.Value.NextCursor;

        }

    }

    private async Task<Result> EraseManagedFilesAsync(
        Guid operationId,
        CovenantArtifactErasureAuthority authority,
        ErasureProgress progress,
        CancellationToken cancellationToken)
    {

        Guid? cursor = null;

        while (true)
        {

            Result<CovenantManagedFileErasureBatch> batch = await _inventory
                .ReadNextManagedFileBatchAsync(operationId, cursor, cancellationToken)
                .ConfigureAwait(false);

            if (batch.IsFailure)
            {

                return Result.Failure(batch.Error);

            }

            if (!batch.Value.IsComplete && batch.Value.NextCursor == cursor)
            {

                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A bounded Covenant managed inventory page did not advance its cursor."));

            }

            foreach (CovenantManagedFileErasureRequest file in batch.Value.Requests)
            {

                progress.EffectAttempted = true;

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

            if (batch.Value.IsComplete)
            {

                return Result.Success();

            }

            cursor = batch.Value.NextCursor;

        }

    }

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
        Func<CovenantErasureCheckpointState, CancellationToken, Task<Result>> step,
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

        Result performed = await step(checkpoint, cancellationToken).ConfigureAwait(false);

        if (performed.IsFailure)
        {

            throw new CovenantErasureStepFailedException(performed.Error);

        }

        if (phase == CovenantResetPhase.CanonicalApplied)
        {

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
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId,
        CovenantExclusiveLease lease,
        CovenantExclusiveLeaseDisposition disposition,
        ErasureProgress progress,
        string? blockingErrorCode)
    {

        using CancellationTokenSource lifecycle = new(DispositionBound, _timeProvider);

        Result closed;

        try
        {

            closed = await lease.CompleteAsync(disposition, lifecycle.Token).ConfigureAwait(false);

        }
        catch (Exception)
        {

            closed = Result.Failure(MaintenanceFailure());

        }


        if (closed.IsFailure)
        {

            Error normalized = MaintenanceFailure();

            bool recoveryProven = await RecordLifecycleFailureAsync(
                operation,
                checkpoint,
                ownerId).ConfigureAwait(false);

            if (recoveryProven)
            {

                _logger.LogError(
                    "A Covenant erasure could not record its {Disposition} disposition ({ErrorCode}); admission "
                    + "stays closed and the operation stays adoptable.",
                    disposition,
                    normalized.Code);

            }
            else
            {

                _logger.LogCritical(
                    "A Covenant erasure could not record its {Disposition} disposition ({ErrorCode}); admission "
                    + "stays closed and durable recovery could not be proven.",
                    disposition,
                    normalized.Code);

            }

            return Result<CovenantErasureCompletion>.Failure(normalized);

        }

        return Result<CovenantErasureCompletion>.Success(
            new CovenantErasureCompletion(
                disposition,
                progress.CanonicalResetApplied,
                progress.LocalSecureErasureComplete,
                progress.Exposure,
                blockingErrorCode));

    }

    private async Task<bool> RecordLifecycleFailureAsync(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId)
    {

        using CancellationTokenSource recording = new(FailureRecordingBound, _timeProvider);

        try
        {

            while (!recording.IsCancellationRequested)
            {

                LongRunningOperation? current = await _store
                    .GetAsync(operation.Id, recording.Token)
                    .ConfigureAwait(false);

                if (current is null)
                {

                    await Task.Delay(
                        FailureRecordingRetryDelay,
                        _timeProvider,
                        recording.Token).ConfigureAwait(false);

                    continue;

                }

                if (IsRecoverableAttention(current, checkpoint))
                {

                    return true;

                }

                if (!HasExactCheckpoint(current, checkpoint))
                {

                    await Task.Delay(
                        FailureRecordingRetryDelay,
                        _timeProvider,
                        recording.Token).ConfigureAwait(false);

                    continue;

                }

                string? transitionOwner;

                if (IsActiveCheckpoint(current, checkpoint, ownerId))
                {

                    transitionOwner = ownerId;

                }
                else if (current.State == LongRunningOperationState.ReconciliationRequired)
                {

                    transitionOwner = null;

                }
                else
                {

                    await Task.Delay(
                        FailureRecordingRetryDelay,
                        _timeProvider,
                        recording.Token).ConfigureAwait(false);

                    continue;

                }

                _ = await _store.TryTransitionAsync(
                    current.Id,
                    current.Revision,
                    transitionOwner,
                    LongRunningOperationState.ReconciliationRequired,
                    _timeProvider.GetUtcNow(),
                    ErrorCodes.Covenant.MaintenanceFailed,
                    recording.Token).ConfigureAwait(false);

                // A successful compare-exchange is not the proof: re-read so the same validation
                // covers our write and an indistinguishable competing winner.

            }

        }
        catch (OperationCanceledException) when (recording.IsCancellationRequested)
        {

            return false;

        }
        catch (Exception)
        {

            return false;

        }

        return false;

    }

    private static bool IsRecoverableAttention(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint) =>
        operation.State == LongRunningOperationState.ReconciliationRequired
        && operation.TerminalErrorCode is ErrorCodes.Covenant.MaintenanceFailed
            or ErrorCodes.Data.ReconciliationFailed
        && HasExactCheckpoint(operation, checkpoint);

    private static bool IsActiveCheckpoint(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint,
        string ownerId)
    {

        if (operation.State is not LongRunningOperationState.Running
            and not LongRunningOperationState.Waiting
            and not LongRunningOperationState.Cancelling
            || !string.Equals(operation.LeaseOwner, ownerId, StringComparison.Ordinal))
        {

            return false;

        }

        return HasExactCheckpoint(operation, checkpoint);

    }

    private static bool HasExactCheckpoint(
        LongRunningOperation operation,
        CovenantErasureCheckpointState checkpoint)
    {

        if (operation.Id != checkpoint.OperationId
            || !string.Equals(
                operation.CheckpointReference,
                CovenantResetCheckpointInitiator.CheckpointReference(operation.Kind, operation.Id),
                StringComparison.Ordinal)
            || operation.CheckpointPayload is not { Length: > 0 } payload)
        {

            return false;

        }

        Result<CovenantErasureCheckpointState> durable = operation.Kind switch
        {

            LongRunningOperationKinds.DataRetentionMutation
                when operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                    && operation.CheckpointVersion == DataRetentionMutationCheckpointV3.CurrentVersion =>
                CovenantErasureCheckpointState.FromMutationCheckpoint(
                    operation.Id,
                    payload,
                    out _),

            LongRunningOperationKinds.DataRetentionFactoryReset
                when operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.RestartIdempotently
                    && operation.CheckpointVersion == DataRetentionFactoryResetCheckpointV1.CurrentVersion =>
                CovenantErasureCheckpointState.FromFactoryResetCheckpoint(operation.Id, payload),

            _ => Result<CovenantErasureCheckpointState>.Failure(MaintenanceFailure()),

        };

        return durable.IsSuccess && durable.Value == checkpoint;

    }

    private static async Task<Result> RunLifecycleAsync(
        Func<CancellationToken, Task<Result>> action,
        CancellationToken cancellationToken)
    {

        try
        {

            return await action(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception)
        {

            return Result.Failure(MaintenanceFailure());

        }

    }

    private static Error MaintenanceFailure() =>
        new(
            ErrorCodes.Covenant.MaintenanceFailed,
            "The Covenant erasure lifecycle could not be completed safely.");

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

        /// <summary>Whether control has crossed from inventory into any protected effect.</summary>
        internal bool EffectAttempted { get; set; }

        internal CovenantDisclosureExposure Exposure { get; set; } =
            new(0, CovenantDisclosureCountKind.Exact);

        /// <summary>
        /// Whether anything irreversible has happened yet, which is the only fact that separates a
        /// reopening abort from one that must keep admission closed.
        /// </summary>
        internal bool DurablyMutated { get; set; } = resumedFrom > CovenantResetPhaseMachine.First;

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
