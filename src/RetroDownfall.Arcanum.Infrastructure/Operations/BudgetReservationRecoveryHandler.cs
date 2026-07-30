using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// A crashed inference cannot safely invent actual provider cost. Releasing the still-reserved
/// amount is idempotent and prevents a reservation from remaining stranded indefinitely.
/// </summary>
internal sealed class BudgetReservationRecoveryHandler(
    IBudgetReservationService reservations) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.BudgetReservation;

    public int SupportedCheckpointVersion => 0;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation.BudgetReservationId is not { } reservationId)
        {
            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.CorruptCheckpoint);
        }

        await reservations.ReleaseAsync(reservationId, cancellationToken).ConfigureAwait(false);
        return LongRunningOperationRecoveryResult.Completed();
    }
}
