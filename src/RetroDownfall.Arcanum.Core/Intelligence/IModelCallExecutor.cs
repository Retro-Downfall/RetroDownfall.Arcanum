using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

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

    /// <summary>Buffered provider invocation with budget begin + purpose metadata.</summary>
    Task<Result<ModelCallResult>> ExecuteBufferedAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken);

    /// <summary>Streaming provider invocation; yields text/response updates then completes.</summary>
    IAsyncEnumerable<ModelCallUpdate> ExecuteStreamingAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken);

}

/// <summary>Default model-call executor — budget gate + provider I/O.</summary>
public sealed class ModelCallExecutor : IModelCallExecutor
{

    public bool TryBeginModelCall(ITurnBudget budget) => budget.TryConsumeModelCall();

    public async Task<Result<ModelCallResult>> ExecuteBufferedAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(budget);

        if (!TryBeginModelCall(budget))
        {
            return Result<ModelCallResult>.Failure(new Error(
                ErrorCodes.Hub.TurnBudgetExceeded,
                "Model call limit reached for this turn."));
        }

        string modelCallId = Guid.NewGuid().ToString("N");

        try
        {
            ChatResponse response = await chatClient
                .GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            return Result<ModelCallResult>.Success(
                new ModelCallResult(purpose, modelCallId, response, response.Usage));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<ModelCallResult>.Failure(new Error(
                ErrorCodes.Hub.Error,
                ex.Message));
        }
    }

    public async IAsyncEnumerable<ModelCallUpdate> ExecuteStreamingAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(budget);

        if (!TryBeginModelCall(budget))
        {
            yield break;
        }

        string modelCallId = Guid.NewGuid().ToString("N");

        await foreach (ChatResponseUpdate update in chatClient
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return new ModelCallResponseUpdate(purpose, modelCallId, update);

            string? text = update.Text;

            if (!string.IsNullOrEmpty(text))
            {
                yield return new ModelCallTextDelta(purpose, modelCallId, text);
            }

            if (update.Contents is { Count: > 0 })
            {
                foreach (AIContent content in update.Contents)
                {
                    if (content is UsageContent usageContent)
                    {
                        yield return new ModelCallUsageUpdate(purpose, modelCallId, usageContent.Details);
                    }
                }
            }
        }
    }

}
