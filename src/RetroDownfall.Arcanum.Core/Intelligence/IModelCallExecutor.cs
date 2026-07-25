using Microsoft.Extensions.AI;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Sole chat-provider invocation boundary for the inference turn pipeline.
/// Returns purpose-tagged updates/results; does not emit Api-layer turn events.
/// </summary>
public interface IModelCallExecutor
{

    /// <summary>
    /// Records that a model call is about to occur against <paramref name="budget"/>. Returns false when
    /// the turn's model-call ceiling is exhausted.
    /// </summary>
    bool TryBeginModelCall(ITurnBudget budget);

    /// <summary>
    /// Buffered provider invocation with budget begin + purpose metadata. Production callers must
    /// supply a model-call context so admission cannot be bypassed.
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
    /// model-call context.
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
