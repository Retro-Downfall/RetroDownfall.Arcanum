using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Sole chat-provider invocation boundary for the inference turn pipeline.
/// Returns purpose-tagged updates/results; does not emit Api-layer turn events.
/// </summary>
public interface IModelCallExecutor
{
    /// <summary>Fixed plaintext Campaign maintenance lane with the physical-send authority fence.</summary>
    Task<Result<CampaignMaintenanceCallResult>> ExecuteCampaignMaintenanceAsync(
        IChatClient chatClient,
        CampaignMaintenancePayload payload,
        ITurnBudget budget,
        Func<CancellationToken, Task> beforeSend,
        CancellationToken cancellationToken,
        ModelCallContext context) =>
        throw new NotSupportedException("This executor does not support closed Campaign maintenance calls.");

    /// <summary>
    /// Buffered provider invocation with purpose metadata. Production callers must supply a
    /// model-call context so context, cache, fingerprint, and cost admission cannot be bypassed.
    /// </summary>
    Task<ModelCallOutcome> ExecuteBufferedAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken,
        ModelCallContext? context);

    /// <summary>
    /// Streaming provider invocation; yields semantic context/answer/reasoning/usage updates before
    /// each corresponding raw response update, then completes. Production callers must supply a
    /// model-call context so non-count admission remains authoritative.
    /// </summary>
    IAsyncEnumerable<ModelCallUpdate> ExecuteStreamingAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken,
        ModelCallContext? context);

}
