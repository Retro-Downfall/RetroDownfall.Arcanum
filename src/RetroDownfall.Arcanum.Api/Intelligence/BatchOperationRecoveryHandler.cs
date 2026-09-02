using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Durable-ledger entry point for batch recovery.
/// </summary>
/// <remarks>
/// The reconciliation itself is <see cref="IBatchRecoveryService.ReconcileStrandedAsync"/>, which
/// host startup already runs before the dispatch loop: every stranded <c>in_progress</c> batch is
/// either re-queued (input still readable) or explicitly failed (input gone), dispatched-but-
/// unrecorded lines are sealed from their durable checkpoints, and partial output artifacts are
/// cleared. Completed lines carry their own checkpoints, so re-queuing never dispatches one twice —
/// which is what makes running this from a recovery pass as well as from startup safe.
///
/// It reconciles by durable batch status rather than by ledger row because that status, not the
/// operation row, is what the dispatch loop reads.
/// </remarks>
internal sealed class BatchOperationRecoveryHandler(
    IBatchRecoveryService recovery,
    ILogger<BatchOperationRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.Batch;

    public int SupportedCheckpointVersion => 0;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await recovery.ReconcileStrandedAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Batch recovery: operation {OperationId} reconciled stranded batches from durable line "
            + "checkpoints; no completed line was dispatched twice.",
            operation.Id);

        return LongRunningOperationRecoveryResult.Completed();
    }
}
