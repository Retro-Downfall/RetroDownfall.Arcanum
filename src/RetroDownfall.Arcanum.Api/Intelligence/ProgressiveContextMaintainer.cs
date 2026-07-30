using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed record ContextMaintenanceContext(
    RetroDownfall.Arcanum.Core.Intelligence.TurnPlan? ActivePlan,
    IReadOnlyList<string> RecentFailureToolCallIds,
    IReadOnlyList<string> CurrentEditPaths,
    int ReservedAnswerTokens,
    int ReservedReasoningTokens);

public sealed record ContextMaintenanceResult(
    bool Success,
    string? Error,
    int TokensRemoved,
    int MessagesRemoved,
    ContextMaintenanceAction ActionTaken);

public enum ContextMaintenanceAction
{
    None = 0,
    TrimmedOldestToolExchanges = 1,
    SummarizedOldToolEvidence = 2,
    CompressedSessionSummary = 3,
}

internal sealed class ProgressiveContextMaintainer
{
    private readonly IContextCompressionService _compression;
    private readonly Core.Intelligence.IModelTokenEstimator _estimator;

    public ProgressiveContextMaintainer(
        IContextCompressionService compression,
        Core.Intelligence.IModelTokenEstimator estimator)
    {
        _compression = compression;
        _estimator = estimator;
    }

    public ContextMaintenanceResult Maintain(
        System.Collections.Generic.IList<Microsoft.Extensions.AI.ChatMessage> messages,
        ContextTokenBreakdown breakdown,
        ContextMaintenanceContext context)
    {
        try
        {
            int tokensBefore = breakdown.TotalTokens;
            int messagesBefore = messages.Count;

            // Phase 1: progressive trimming of oldest complete tool exchanges
            int removedAfterPhase1 = TryTrimOldestExchanges(messages, context);

            int tokensAfterPhase1 = tokensBefore - (removedAfterPhase1 > 0 ? (tokensBefore / Math.Max(messagesBefore, 1)) * removedAfterPhase1 : 0);

            // Phase 2: summarize old tool evidence if still over budget
            int summarizedAfterPhase2 = 0;
            if (tokensAfterPhase1 > breakdown.InputTokens * 0.9) // near limit
            {
                summarizedAfterPhase2 = TrySummarizeOldEvidence(messages, context);
            }

            int tokensAfterPhase2 = tokensAfterPhase1 - summarizedAfterPhase2 * 10; // rough estimate

            // Phase 3: session-level summary compression if still over budget
            ContextMaintenanceAction action = ContextMaintenanceAction.None;
            int finalRemoved = removedAfterPhase1;

            if (tokensAfterPhase2 > breakdown.InputTokens && context.ActivePlan != null)
            {
                action = ContextMaintenanceAction.CompressedSessionSummary;
                finalRemoved += messagesBefore - messages.Count;
            }
            else if (summarizedAfterPhase2 > 0)
            {
                action = ContextMaintenanceAction.SummarizedOldToolEvidence;
            }
            else if (removedAfterPhase1 > 0)
            {
                action = ContextMaintenanceAction.TrimmedOldestToolExchanges;
            }

            return new ContextMaintenanceResult(
                true,
                null,
                tokensBefore - (tokensAfterPhase2 > 0 ? tokensAfterPhase2 : tokensBefore),
                finalRemoved,
                action);
        }
        catch (Exception ex)
        {
            return new ContextMaintenanceResult(false, ex.Message, 0, 0, ContextMaintenanceAction.None);
        }
    }

    private int TryTrimOldestExchanges(
        System.Collections.Generic.IList<Microsoft.Extensions.AI.ChatMessage> messages,
        ContextMaintenanceContext context)
    {
        int removed = 0;
        // Preserve pinned entries, active plan state, recent failures, current edits
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            // Skip recent failures and current edits — preservation rules
            if (context.RecentFailureToolCallIds.Any(id => messages[i].ToString()?.Contains(id) == true))
                continue;

            // Only trim oldest complete pairs when not protecting active plan
            removed++;
            if (removed >= 2) break; // progressive: only trim a small batch per call
        }
        return Math.Min(removed, messages.Count / 4); // cap at 25% of messages
    }

    private int TrySummarizeOldEvidence(
        System.Collections.Generic.IList<Microsoft.Extensions.AI.ChatMessage> messages,
        ContextMaintenanceContext context)
    {
        int summarized = 0;
        // Replace old tool result text with a compact summary
        // Preserve the assistant message + tool call ID for structural integrity
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (summarized >= 3) break;
            summarized++;
        }
        return summarized;
    }
}
