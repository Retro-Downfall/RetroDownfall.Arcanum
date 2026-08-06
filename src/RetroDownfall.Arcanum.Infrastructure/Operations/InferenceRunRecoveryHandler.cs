using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Resolves an inference run that was in flight when the host died (#40, requirement 3).
/// </summary>
/// <remarks>
/// A crashed stream cannot be replayed and its provider cost cannot be invented: whatever the
/// provider actually billed is only knowable from the <c>BillableOperations</c> rows the turn wrote
/// before it died, and those are already ledgered exactly once. Recovery therefore does three
/// narrow things — settle the run row, stop the reservation consuming the daily limit, and make a
/// half-captured response non-replayable — and claims nothing else.
/// </remarks>
internal sealed class InferenceRunRecoveryHandler(
    ITurnRunWriter runs,
    IBudgetReservationService reservations,
    IIdempotencyClaimStore claims,
    TimeProvider timeProvider,
    ILogger<InferenceRunRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    /// <summary>Short window: the claim is being settled, not executed.</summary>
    private static readonly TimeSpan ReclaimLease = TimeSpan.FromMinutes(2);

    public string Kind => LongRunningOperationKinds.InferenceRun;

    public int SupportedCheckpointVersion => 0;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.InferenceRunId is not { } runId)
        {
            logger.LogWarning(
                "Durable operation {OperationId} claims to own an inference run but carries no run id.",
                operation.Id);

            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.MissingOperationLink);
        }

        bool abandoned = await runs.TryAbandonRunAsync(runId, cancellationToken).ConfigureAwait(false);

        if (abandoned)
        {
            logger.LogInformation(
                "Inference recovery: run {RunId} was interrupted and is now abandoned. Usage already "
                + "ledgered before the crash is retained; nothing was re-billed.",
                runId);
        }

        // Unconditional and idempotent: a pass that died between abandoning the run and releasing its
        // reservation must not leave the daily budget permanently consumed by a dead process.
        if (operation.BudgetReservationId is { } reservationId)
        {
            await reservations.ReleaseAsync(reservationId, cancellationToken).ConfigureAwait(false);
        }

        if (operation.IdempotencyClaimId is { } claimId)
        {
            await IdempotencyClaimRecoveryHandler
                .SettleClaimAsync(claims, claimId, timeProvider, ReclaimLease, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        return LongRunningOperationRecoveryResult.Completed();
    }
}
