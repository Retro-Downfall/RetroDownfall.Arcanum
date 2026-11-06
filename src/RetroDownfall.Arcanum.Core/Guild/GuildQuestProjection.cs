using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>Committed question identity, never inferred from a generic ledger wait.</summary>
public sealed record GuildQuestInputEvidence(Guid QuestionId, Guid ContinuationId);

/// <summary>Committed observations used only to project the authoritative operation ledger.</summary>
public sealed record GuildQuestProjectionEvidence(
    Guid QuestId,
    Guid OperationId,
    QuestState LastNonterminalState,
    GuildWaitReason? WaitReason = null,
    GuildQuestInputEvidence? AwaitingInput = null,
    GuildResultReceipt? ResultReceipt = null,
    bool NoDispatchProven = false,
    bool WorkStoppedProven = false,
    bool SafelyAbandoned = false,
    bool CancellationRequested = false,
    bool CancellationConverged = false);

public sealed record GuildQuestProjectedState(
    QuestState State,
    GuildWaitReason? WaitReason,
    bool CancellationRequested,
    bool CancellationConverged,
    bool ReconciliationRequired,
    string? ReasonCode);

/// <summary>Pure projection; the operation ledger remains the only execution lifecycle.</summary>
public static class GuildQuestProjection
{
    public static GuildQuestProjectedState Project(LongRunningOperation operation, GuildQuestProjectionEvidence evidence)
    {
        bool cancellationRequested = evidence.CancellationRequested
            || operation.State == LongRunningOperationState.Cancelling
            || operation.TerminalErrorCode == LongRunningOperationRecoveryOutcomes.Cancelled;

        evidence = evidence with { CancellationRequested = cancellationRequested };

        if (!IsNonterminal(evidence.LastNonterminalState))
            return Uncertain(evidence with { LastNonterminalState = QuestState.Working });

        if (evidence.QuestId == Guid.Empty || operation.Id == Guid.Empty
            || evidence.OperationId != operation.Id || operation.Kind != "guild-quest")
            return Uncertain(evidence);

        GuildResultReceipt? receipt = evidence.ResultReceipt;

        if (receipt is not null && !ReceiptMatches(evidence, receipt))
            return Uncertain(evidence);

        bool terminal = operation.State is LongRunningOperationState.Completed or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned;

        if (!terminal && receipt is not null)
            return Uncertain(evidence);

        return operation.State switch
        {
            LongRunningOperationState.Pending => new(QuestState.Submitted, null, cancellationRequested, false, false, null),
            LongRunningOperationState.Running => new(QuestState.Working, null, cancellationRequested, false, false, null),
            LongRunningOperationState.Waiting when evidence.WaitReason == GuildWaitReason.Input =>
                evidence.AwaitingInput is { QuestionId: var question, ContinuationId: var continuation }
                && question != Guid.Empty && continuation != Guid.Empty
                    ? new(QuestState.InputRequired, GuildWaitReason.Input, cancellationRequested, false, false, null)
                    : Uncertain(evidence),
            LongRunningOperationState.Waiting when evidence.LastNonterminalState is QuestState.Submitted or QuestState.Working
                && evidence.WaitReason is GuildWaitReason.Maintenance or GuildWaitReason.Budget or GuildWaitReason.Transport =>
                new(evidence.LastNonterminalState, evidence.WaitReason, cancellationRequested, false, false, null),
            LongRunningOperationState.Cancelling => new(evidence.LastNonterminalState, evidence.WaitReason, true, false, false, null),
            LongRunningOperationState.Completed when operation.TerminalErrorCode is null
                && receipt is { Outcome: QuestState.Completed, ReasonCode: null } =>
                new(QuestState.Completed, null, cancellationRequested, Converged(evidence), false, null),
            LongRunningOperationState.Completed when operation.TerminalErrorCode == LongRunningOperationRecoveryOutcomes.Cancelled
                && evidence.WorkStoppedProven
                && receipt is { Outcome: QuestState.Canceled, ReasonCode: LongRunningOperationRecoveryOutcomes.Cancelled } =>
                new(QuestState.Canceled, null, true, true, false, LongRunningOperationRecoveryOutcomes.Cancelled),
            LongRunningOperationState.Failed when operation.TerminalErrorCode == ErrorCodes.Guild.Rejected
                && evidence.NoDispatchProven
                && (receipt is null || receipt is { Outcome: QuestState.Rejected, ReasonCode: ErrorCodes.Guild.Rejected }) =>
                new(QuestState.Rejected, null, cancellationRequested, Converged(evidence), false, ErrorCodes.Guild.Rejected),
            LongRunningOperationState.Failed when HasNamedFailure(operation)
                && receipt is { Outcome: QuestState.Failed }
                && receipt.ReasonCode == operation.TerminalErrorCode =>
                new(QuestState.Failed, null, cancellationRequested, Converged(evidence), false, operation.TerminalErrorCode),
            LongRunningOperationState.Abandoned when HasNamedFailure(operation)
                && evidence.SafelyAbandoned
                && (receipt is null || receipt.Outcome == QuestState.Failed && receipt.ReasonCode == operation.TerminalErrorCode) =>
                new(QuestState.Failed, null, cancellationRequested, Converged(evidence), false, operation.TerminalErrorCode),
            _ => Uncertain(evidence)
        };
    }

    // This is a separately verified domain fact, never inferred from a cancellation request.
    private static bool Converged(GuildQuestProjectionEvidence evidence) =>
        evidence.CancellationConverged && evidence.CancellationRequested && evidence.ResultReceipt is not null;

    private static bool IsNonterminal(QuestState state) =>
        state is QuestState.Submitted or QuestState.Working or QuestState.InputRequired;

    private static bool HasNamedFailure(LongRunningOperation operation) =>
        !string.IsNullOrWhiteSpace(operation.TerminalErrorCode)
        && operation.TerminalErrorCode is not LongRunningOperationRecoveryOutcomes.Cancelled and not ErrorCodes.Guild.Rejected and not ErrorCodes.Guild.ReconciliationRequired;

    private static bool ReceiptMatches(GuildQuestProjectionEvidence evidence, GuildResultReceipt receipt) =>
        receipt.QuestId == evidence.QuestId
        && receipt.ResultId != Guid.Empty
        && !string.IsNullOrWhiteSpace(receipt.CanonicalContentDigest)
        && (receipt.Content is null || receipt.Content.Kind == GuildContentKind.QuestResult
            && receipt.Content.Digest == receipt.CanonicalContentDigest);

    private static GuildQuestProjectedState Uncertain(GuildQuestProjectionEvidence evidence) =>
        new(evidence.LastNonterminalState, GuildWaitReason.Reconciliation, evidence.CancellationRequested, false, true, ErrorCodes.Guild.ReconciliationRequired);
}
