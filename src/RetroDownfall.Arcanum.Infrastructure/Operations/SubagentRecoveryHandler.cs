using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Abandons an isolated subagent child that died with its host (#40, requirement 8).
/// </summary>
/// <remarks>
/// The child's conversational context lived entirely in process: its tracker, its ambient isolation
/// scope, and the parent's awaiting call are all gone. Restarting from the ledger row would re-run a
/// billable child that nobody is waiting for, and a parent that also recovers would spawn it again —
/// exactly the restart storm the issue forbids. So this handler abandons, releases the child's
/// reservation, and stops.
///
/// Cost is deliberately *not* aggregated here. Whatever the child actually spent was written to
/// <c>BillableOperations</c> as it went, already exactly once; adding a recovery-time estimate on
/// top would double-count real money against a number Arcanum cannot verify.
/// </remarks>
internal sealed class SubagentRecoveryHandler(
    IBudgetReservationService reservations,
    ITurnRunWriter runs,
    ILogger<SubagentRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.Subagent;

    public int SupportedCheckpointVersion => 0;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.BudgetReservationId is { } reservationId)
        {
            await reservations.ReleaseAsync(reservationId, cancellationToken).ConfigureAwait(false);
        }

        // A child that reached the inference ledger is settled with the same compare-and-set guard the
        // parent turn uses, so a child that genuinely finished is not rewritten.
        if (operation.InferenceRunId is { } runId)
        {
            _ = await runs.TryAbandonRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Subagent recovery: child run {ChildRunId} is abandoned rather than restarted. Any cost it "
            + "already incurred stays ledgered where the child wrote it.",
            operation.RunId);

        return LongRunningOperationRecoveryResult.Abandoned(
            LongRunningOperationRecoveryOutcomes.SubagentChildAbandoned);
    }
}
