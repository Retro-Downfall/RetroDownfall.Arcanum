using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The single owner of accelerator-rebuild operation identity, checkpoints, and recovery scheduling.
/// </summary>
/// <remarks>
/// It owns none of the algorithm. Base scanning, delta application, rank-1 integrity, and eligibility
/// publication belong to <see cref="CovenantIndexRebuilder"/> and are reached through its one
/// <c>AdvanceBatchAsync</c> step, because a coordinator that could also decide what a batch means
/// would be a second rebuild implementation with a different idea of when the index is publishable
/// (§10.17).
///
/// <para>Identity is always server-generated. A rebuild replaces a derived accelerator, so there is no
/// authenticated preflight to name it after and nothing for a caller to bind an apply request to —
/// which is exactly why it never reaches <see cref="CovenantRequestedOperationStarter"/>.</para>
/// </remarks>
internal sealed class CovenantIndexRebuildCoordinator(
    ILongRunningOperationCoordinator operations,
    ILongRunningOperationStore store,
    ICovenantOperationGate gate,
    CovenantIndexRebuilder rebuilder,
    TimeProvider timeProvider)
{

    /// <summary>How long one batch owns its ledger lease before another worker may claim it.</summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private const string Summary = "Rebuilding the Covenant inspection index.";

    private readonly ILongRunningOperationCoordinator _operations =
        operations ?? throw new ArgumentNullException(nameof(operations));

    private readonly ILongRunningOperationStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly ICovenantOperationGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    private readonly CovenantIndexRebuilder _rebuilder =
        rebuilder ?? throw new ArgumentNullException(nameof(rebuilder));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Starts one rebuild under a server-generated identity.
    /// </summary>
    internal async Task<Result<LongRunningOperation>> StartAsync(
        string ownerId,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        LongRunningOperationCreateRequest request = new(
            LongRunningOperationKinds.CovenantIndexRebuild,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            Summary,
            _time.GetUtcNow());

        LongRunningOperationLeaseResult started = await _operations
            .StartAsync(request, ownerId, LeaseDuration, cancellationToken)
            .ConfigureAwait(false);

        return Result<LongRunningOperation>.Success(started.Operation);

    }

    /// <summary>
    /// Advances one batch and records the exact cursor the rebuilder returned.
    /// </summary>
    /// <remarks>
    /// The checkpoint is written before the accelerator lease is released and after the batch's own
    /// transaction commits, so a crash between them resumes from a cursor the database already agrees
    /// with. A <c>RestartRequired</c> phase terminalizes this operation with a content-free code and
    /// starts a new one rather than rewriting this operation's identity: the stale identity is the
    /// evidence of what was being rebuilt when the ground moved.
    /// </remarks>
    internal async Task<Result<CovenantIndexRebuildProgress>> AdvanceAsync(
        LongRunningOperation operation,
        string ownerId,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        Result<CovenantIndexRebuildProgress?> decoded = DecodeCheckpoint(operation);

        if (decoded.IsFailure)
        {

            return Result<CovenantIndexRebuildProgress>.Failure(decoded.Error);

        }

        Result<CovenantAcceleratorLease> lease = await _gate
            .AcquireAcceleratorAsync(cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsFailure)
        {

            return Result<CovenantIndexRebuildProgress>.Failure(lease.Error);

        }

        await using CovenantAcceleratorLease accelerator = lease.Value;

        Result<CovenantIndexRebuildProgress> advanced = await _rebuilder
            .AdvanceBatchAsync(decoded.Value, accelerator, cancellationToken)
            .ConfigureAwait(false);

        if (advanced.IsFailure)
        {

            return advanced;

        }

        Result saved = await SaveCheckpointAsync(operation, ownerId, advanced.Value, cancellationToken)
            .ConfigureAwait(false);

        return saved.IsFailure
            ? Result<CovenantIndexRebuildProgress>.Failure(saved.Error)
            : advanced;

    }

    /// <summary>
    /// Reads the operation's durable cursor, or reports that this build cannot resume it.
    /// </summary>
    internal static Result<CovenantIndexRebuildProgress?> DecodeCheckpoint(LongRunningOperation operation)
    {

        ArgumentNullException.ThrowIfNull(operation);

        if (operation.CheckpointPayload is not { } payload)
        {

            return Result<CovenantIndexRebuildProgress?>.Success(null);

        }

        Result<CovenantIndexRebuildCheckpointV1> checkpoint =
            CovenantRecoveryCheckpointCodec.DecodeIndexRebuild(payload);

        if (checkpoint.IsFailure)
        {

            return Result<CovenantIndexRebuildProgress?>.Failure(checkpoint.Error);

        }

        // Copied field for field. A projection that renamed or reinterpreted anything would become a
        // second cursor model, and the two would eventually disagree about where the scan is.
        return Result<CovenantIndexRebuildProgress?>.Success(
            new CovenantIndexRebuildProgress(
                checkpoint.Value.DatasetGeneration,
                checkpoint.Value.AcceleratorEpoch,
                checkpoint.Value.BaseTargetSearchSequence,
                checkpoint.Value.CapturedCoreCampaignDeletionSequence,
                checkpoint.Value.Phase,
                checkpoint.Value.BaseScanAfterSearchRowId,
                checkpoint.Value.LastContiguousAppliedSequence,
                checkpoint.Value.BaseHeadsProcessed,
                checkpoint.Value.BaseHeadsTotal,
                checkpoint.Value.DeltaRowsProcessed));

    }

    internal static CovenantIndexRebuildCheckpointV1 ToCheckpoint(CovenantIndexRebuildProgress progress)
    {

        ArgumentNullException.ThrowIfNull(progress);

        return new CovenantIndexRebuildCheckpointV1(
            CovenantIndexRebuildCheckpointV1.CurrentVersion,
            progress.DatasetGeneration,
            progress.AcceleratorEpoch,
            progress.BaseTargetSearchSequence,
            progress.CapturedCoreCampaignDeletionSequence,
            progress.Phase,
            progress.BaseScanAfterSearchRowId,
            progress.LastContiguousAppliedSequence,
            progress.BaseHeadsProcessed,
            progress.BaseHeadsTotal,
            progress.DeltaRowsProcessed);

    }

    private async Task<Result> SaveCheckpointAsync(
        LongRunningOperation operation,
        string ownerId,
        CovenantIndexRebuildProgress progress,
        CancellationToken cancellationToken)
    {

        bool saved = await _operations.CheckpointAsync(
            operation.Id,
            ownerId,
            operation.CheckpointVersion,
            CovenantIndexRebuildCheckpointV1.CurrentVersion,
            CovenantRecoveryCheckpointCodec.Encode(ToCheckpoint(progress)),
            checkpointReference: null,
            Summary,
            cancellationToken).ConfigureAwait(false);

        if (!saved)
        {

            return Result.Failure(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    "The Covenant index rebuild checkpoint was written by another owner."));

        }

        return progress.Phase switch
        {

            CovenantIndexRebuildPhase.Completed => await FinishAsync(operation, ownerId, cancellationToken)
                .ConfigureAwait(false),

            CovenantIndexRebuildPhase.RestartRequired => await AbandonStaleAsync(operation, ownerId, cancellationToken)
                .ConfigureAwait(false),

            _ => Result.Success(),

        };

    }

    private async Task<Result> FinishAsync(
        LongRunningOperation operation,
        string ownerId,
        CancellationToken cancellationToken)
    {

        LongRunningOperation? current = await _store.GetAsync(operation.Id, cancellationToken).ConfigureAwait(false);

        if (current is null)
        {

            return Result.Success();

        }

        _ = await _operations
            .CompleteAsync(operation.Id, ownerId, current.Revision, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success();

    }

    private async Task<Result> AbandonStaleAsync(
        LongRunningOperation operation,
        string ownerId,
        CancellationToken cancellationToken)
    {

        LongRunningOperation? current = await _store.GetAsync(operation.Id, cancellationToken).ConfigureAwait(false);

        if (current is not null)
        {

            _ = await _store.TryTransitionAsync(
                operation.Id,
                current.Revision,
                ownerId,
                LongRunningOperationState.Abandoned,
                _time.GetUtcNow(),
                CovenantIndexRebuildRestartCode,
                cancellationToken).ConfigureAwait(false);

        }

        _ = await StartAsync(ownerId, cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    /// <summary>
    /// The content-free terminal code a stale rebuild closes under.
    /// </summary>
    internal const string CovenantIndexRebuildRestartCode = "covenant.index_rebuild_identity_changed";

}

/// <summary>
/// The one registered recovery handler for an interrupted accelerator rebuild.
/// </summary>
/// <remarks>
/// Recovery does not run the rebuild. It decodes the durable cursor and either hands the operation
/// back as resumable work or closes it: the accelerator is a derived index, the canonical prompt path
/// never depends on it, and a startup pass that rebuilt a whole index inline would delay readiness for
/// something no correctness property needs.
/// </remarks>
internal sealed class CovenantIndexRebuildRecoveryHandler : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.CovenantIndexRebuild;

    public int SupportedCheckpointVersion => CovenantIndexRebuildCheckpointV1.CurrentVersion;

    public Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        Result<CovenantIndexRebuildProgress?> decoded = CovenantIndexRebuildCoordinator.DecodeCheckpoint(operation);

        if (decoded.IsFailure)
        {

            return Task.FromResult(
                LongRunningOperationRecoveryResult.Abandoned(LongRunningOperationErrorCodes.CorruptCheckpoint));

        }

        // A crashed rebuild with no checkpoint never committed a batch, and one whose captured identity
        // is stale is not resumable against the current dataset. Both close cleanly; the operator's
        // remedy is the same rebuild command in either case.
        return Task.FromResult(
            decoded.Value is { Phase: CovenantIndexRebuildPhase.Completed }
                ? LongRunningOperationRecoveryResult.Completed()
                : LongRunningOperationRecoveryResult.Abandoned(
                    CovenantIndexRebuildCoordinator.CovenantIndexRebuildRestartCode));

    }

}
