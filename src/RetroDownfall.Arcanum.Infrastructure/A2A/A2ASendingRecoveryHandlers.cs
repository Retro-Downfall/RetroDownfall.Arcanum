using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Named outcomes recorded when a Sending is reconciled after a restart.
/// </summary>
/// <remarks>
/// Issue #62 requires that an in-flight Sending across a restart is either continued or "explicitly
/// failed with a reason", and that an outbound one records whether the remote task was cancelled or
/// abandoned. These are those reasons, carried on the ledger row's terminal error code so
/// <c>arcanum operations</c> shows what actually happened rather than a bare "abandoned".
/// </remarks>
public static class A2ASendingRecoveryOutcomes
{

    /// <summary>The Apprentice survived, but the A2A correspondence could not be re-established.</summary>
    public const string InboundRelayAbandoned = "a2a.inbound_relay_abandoned";

    /// <summary>The Apprentice behind an inbound Sending is gone; nothing is left to report on.</summary>
    public const string InboundApprenticeMissing = "a2a.inbound_apprentice_missing";

    /// <summary>The remote task was successfully cancelled on the far side.</summary>
    public const string OutboundRemoteCancelled = "a2a.outbound_remote_cancelled";

    /// <summary>The remote task could not be reached; it may still be running and billing.</summary>
    public const string OutboundRemoteAbandoned = "a2a.outbound_remote_abandoned";

}

/// <summary>
/// Reconciles inbound A2A Sendings that were in flight when the process died.
/// </summary>
/// <remarks>
/// The peer's HTTP connection and the A2A SDK's task store are both process-local, so the relay itself
/// cannot be resurrected — an inbound task is therefore explicitly abandoned with a named reason rather
/// than left silently in <c>Working</c> forever. The Apprentice is untouched: it is durable, it resumes
/// on its own (&#167;5.7), and it remains cancellable through <c>arcanum apprentice cancel</c> or a peer
/// <c>tasks/cancel</c>, which now resolves through this same durable record (issue #62).
/// </remarks>
internal sealed class A2AInboundSendingRecoveryHandler(
    IApprenticeRepository apprentices,
    ILogger<A2AInboundSendingRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.A2AInboundSending;

    public int SupportedCheckpointVersion => A2ASendingLedger.CheckpointVersion;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        if (A2ASendingLedger.TryRead(operation) is not { } record || record.ApprenticeId is not { } apprenticeId)
        {

            return LongRunningOperationRecoveryResult.Abandoned(A2ASendingRecoveryOutcomes.InboundApprenticeMissing);

        }

        Apprentice? apprentice = await apprentices
            .GetByIdAsync(apprenticeId, cancellationToken)
            .ConfigureAwait(false);

        if (apprentice is null)
        {

            logger.LogInformation(
                "A2A reconciliation: inbound task {TaskId} referenced Apprentice {ApprenticeId}, which no longer exists.",
                record.TaskId,
                apprenticeId);

            return LongRunningOperationRecoveryResult.Abandoned(A2ASendingRecoveryOutcomes.InboundApprenticeMissing);

        }

        logger.LogInformation(
            "A2A reconciliation: inbound task {TaskId} lost its peer relay across a restart. Apprentice "
            + "{ApprenticeId} is durable and unaffected; the A2A task state will not advance further.",
            record.TaskId,
            apprenticeId);

        return LongRunningOperationRecoveryResult.Abandoned(A2ASendingRecoveryOutcomes.InboundRelayAbandoned);

    }

}

/// <summary>
/// Reconciles outbound A2A Sendings that were in flight when the process died, by cancelling the remote
/// task rather than leaving it running and billing on someone else's hardware.
/// </summary>
/// <remarks>
/// This is the half that actually costs money: before issue #62 there was no durable record of the remote
/// task id at all, so an abandoned Sending could not even be named, let alone stopped. The result
/// distinguishes a confirmed cancel from an unreachable peer so an operator can tell which happened.
/// </remarks>
internal sealed class A2AOutboundSendingRecoveryHandler(
    IA2AClientService client,
    ILogger<A2AOutboundSendingRecoveryHandler> logger) : ILongRunningOperationRecoveryHandler
{

    public string Kind => LongRunningOperationKinds.A2AOutboundSending;

    public int SupportedCheckpointVersion => A2ASendingLedger.CheckpointVersion;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(operation);

        if (A2ASendingLedger.TryRead(operation) is not { } record
            || string.IsNullOrWhiteSpace(record.AgentUrl))
        {

            return LongRunningOperationRecoveryResult.Abandoned(A2ASendingRecoveryOutcomes.OutboundRemoteAbandoned);

        }

        Result cancelled = await client
            .CancelRemoteTaskAsync(record.AgentUrl!, record.TaskId, cancellationToken)
            .ConfigureAwait(false);

        if (cancelled.IsSuccess)
        {

            logger.LogInformation(
                "A2A reconciliation: cancelled orphaned remote task {TaskId} on {AgentUrl}.",
                record.TaskId,
                record.AgentUrl);

            return LongRunningOperationRecoveryResult.Completed();

        }

        logger.LogWarning(
            "A2A reconciliation: could not cancel orphaned remote task {TaskId} on {AgentUrl} ({Reason}). "
            + "It may still be running on the remote agent.",
            record.TaskId,
            record.AgentUrl,
            cancelled.Error.Message);

        return LongRunningOperationRecoveryResult.Abandoned(A2ASendingRecoveryOutcomes.OutboundRemoteAbandoned);

    }

}
