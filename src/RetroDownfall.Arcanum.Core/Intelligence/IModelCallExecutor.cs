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
    Task<ModelCallOutcome> ExecuteBufferedAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streaming provider invocation; yields semantic answer/reasoning/usage updates before each
    /// corresponding raw response update, then completes.
    /// </summary>
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

    public async Task<ModelCallOutcome> ExecuteBufferedAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budget);

        if (!TryBeginModelCall(budget))
        {
            return ModelCallOutcome.Failed(new ModelCallFailure(
                purpose,
                string.Empty,
                new Error(
                    ErrorCodes.Hub.TurnBudgetExceeded,
                    "Model call limit reached for this turn."),
                Cause: null));
        }

        string modelCallId = Guid.NewGuid().ToString("N");

        try
        {
            ChatResponse response = await chatClient
                .GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
            (ReasoningOutputMode? requestedOutput, ReasoningOutputMode effectiveOutput) =
                ResolveReasoningOutput(options, purpose);
            ModelCallReasoningResult reasoning = ExtractReasoning(
                response,
                requestedOutput,
                effectiveOutput);

            return ModelCallOutcome.Success(
                new ModelCallResult(purpose, modelCallId, response, response.Usage, reasoning));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModelCallOutcome.Failed(new ModelCallFailure(
                purpose,
                modelCallId,
                new Error(ErrorCodes.Hub.Error, ex.Message),
                ex));
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
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budget);

        if (!TryBeginModelCall(budget))
        {
            yield break;
        }

        string modelCallId = Guid.NewGuid().ToString("N");
        (ReasoningOutputMode? requestedOutput, ReasoningOutputMode effectiveOutput) =
            ResolveReasoningOutput(options, purpose);

        await foreach (ChatResponseUpdate update in chatClient
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (update.Contents is { Count: > 0 })
            {
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent { Text.Length: > 0 } text:
                            yield return new ModelCallTextDelta(purpose, modelCallId, text.Text);
                            break;

                        case TextReasoningContent reasoning:
                            yield return new ModelCallReasoningUpdate(
                                purpose,
                                modelCallId,
                                effectiveOutput == ReasoningOutputMode.None
                                    ? string.Empty
                                    : reasoning.Text ?? string.Empty,
                                requestedOutput,
                                effectiveOutput,
                                !string.IsNullOrEmpty(reasoning.ProtectedData));
                            break;

                        case UsageContent usageContent:
                            yield return new ModelCallUsageUpdate(purpose, modelCallId, usageContent.Details);
                            break;
                    }
                }
            }

            // Semantic updates are emitted first so provider commitment is recorded before any raw
            // response update can be projected to a client.
            yield return new ModelCallResponseUpdate(purpose, modelCallId, update);
        }
    }

    private static ModelCallReasoningResult ExtractReasoning(
        ChatResponse response,
        ReasoningOutputMode? requestedOutput,
        ReasoningOutputMode effectiveOutput)
    {
        ModelCallReasoningAccumulator segments = new();
        bool hasProviderContent = false;
        bool hasProtectedData = false;

        foreach (ChatMessage message in response.Messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is not TextReasoningContent reasoning)
                {
                    continue;
                }

                hasProviderContent = true;
                bool segmentHasProtectedData = !string.IsNullOrEmpty(reasoning.ProtectedData);
                hasProtectedData |= segmentHasProtectedData;
                string visibleText = effectiveOutput == ReasoningOutputMode.None
                    ? string.Empty
                    : reasoning.Text ?? string.Empty;

                if (visibleText.Length > 0 || segmentHasProtectedData)
                {
                    segments.Append(
                        visibleText,
                        requestedOutput,
                        effectiveOutput,
                        segmentHasProtectedData);
                }
            }
        }

        return new ModelCallReasoningResult(
            segments.Materialize(),
            requestedOutput,
            effectiveOutput,
            hasProviderContent,
            hasProtectedData);
    }

    private static (ReasoningOutputMode? Requested, ReasoningOutputMode Effective) ResolveReasoningOutput(
        ChatOptions options,
        ModelCallPurpose purpose)
    {
        ReasoningOutputMode? requested = options.Reasoning?.Output switch
        {
            null => null,
            ReasoningOutput.None => ReasoningOutputMode.None,
            ReasoningOutput.Summary => ReasoningOutputMode.Summary,
            ReasoningOutput.Full => ReasoningOutputMode.Full,
            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Reasoning.Output,
                "Unknown reasoning output mode."),
        };

        bool clientFacing = purpose is ModelCallPurpose.MainInference
            or ModelCallPurpose.ToolContinuation
            or ModelCallPurpose.ToolCompatibilityRetry
            or ModelCallPurpose.StructuredOutputRetry;

        ReasoningOutputMode effective = clientFacing
            ? requested switch
            {
                ReasoningOutputMode.None => ReasoningOutputMode.None,
                ReasoningOutputMode.Summary => ReasoningOutputMode.Summary,
                ReasoningOutputMode.Full or null => ReasoningOutputMode.Full,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(options),
                    requested,
                    "Unknown reasoning output mode."),
            }
            : ReasoningOutputMode.None;

        return (requested, effective);
    }

}
