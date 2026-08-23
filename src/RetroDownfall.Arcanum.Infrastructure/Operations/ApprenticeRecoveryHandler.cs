using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Conclave;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Hands a crashed Apprentice back to its own documented checkpoint resume path (#40).
/// </summary>
/// <remarks>
/// Unlike every other kind here, an Apprentice already survives a restart on its own: its plan, step
/// position, completed tool-call ids, and lineage live in <c>CheckpointData</c>, and
/// <see cref="IApprenticeRepository.GetResumableAsync"/> is what actually restarts it (§5.7). The
/// ledger row exists so the work is *visible*, not so a second mechanism can drive it — resuming it
/// from here would run the same step twice. Recovery therefore verifies the Apprentice is still
/// there and closes the row; a row that outlived its Apprentice is abandoned with a named reason.
/// </remarks>
internal sealed class ApprenticeRecoveryHandler(
    IApprenticeRepository apprentices,
    ILogger<ApprenticeRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.Apprentice;

    public int SupportedCheckpointVersion => 1;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.RunId is not { } apprenticeId)
        {
            logger.LogWarning(
                "Durable operation {OperationId} claims to own an Apprentice but carries no Apprentice id.",
                operation.Id);

            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.MissingOperationLink);
        }

        Apprentice? apprentice = await apprentices
            .GetByIdAsync(apprenticeId, cancellationToken)
            .ConfigureAwait(false);

        if (apprentice is null)
        {
            logger.LogInformation(
                "Apprentice recovery: operation {OperationId} referenced Apprentice {ApprenticeId}, which no "
                + "longer exists.",
                operation.Id,
                apprenticeId);

            return LongRunningOperationRecoveryResult.Abandoned(
                LongRunningOperationRecoveryOutcomes.ApprenticeMissing);
        }

        logger.LogInformation(
            "Apprentice recovery: Apprentice {ApprenticeId} is durable and resumes from its own checkpoint "
            + "at step {CurrentStep}; the ledger row is closed rather than replayed.",
            apprenticeId,
            apprentice.CurrentStep);

        return LongRunningOperationRecoveryResult.Completed();
    }
}
