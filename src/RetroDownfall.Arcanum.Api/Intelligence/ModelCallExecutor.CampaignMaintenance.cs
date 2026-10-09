using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelCallExecutor
{
    public async Task<Result<CampaignMaintenanceCallResult>> ExecuteCampaignMaintenanceAsync(
        IChatClient chatClient,
        CampaignMaintenancePayload payload,
        ITurnBudget budget,
        Func<CancellationToken, Task> beforeSend,
        CancellationToken cancellationToken,
        ModelCallContext context)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(beforeSend);
        ArgumentNullException.ThrowIfNull(context);

        if (_tokenEstimator is null
            || context.PrecomputedBreakdown is not { } precomputed
            || context.ReservedAnswerTokens != CampaignMaintenancePayload.MaxOutputTokens
            || context.ReservedReasoningTokens != 0
            || context.PromptCachePlan is not null)
        {
            return ClosedCampaignAdmissionError();
        }

        // Recompute only the closed plaintext payload. The authoritative components stay
        // code-owned instead of enumerating a caller-supplied breakdown or SDK metadata.
        ContextTokenBreakdown breakdown = _tokenEstimator.EstimateCampaignMaintenance(context.Provider, context.Model, payload);

        if (!string.Equals(precomputed.Provider, breakdown.Provider, StringComparison.Ordinal)
            || !string.Equals(precomputed.Model, breakdown.Model, StringComparison.Ordinal)
            || precomputed.Profile != breakdown.Profile
            || precomputed.ReservedTokens != breakdown.ReservedTokens
            || precomputed.ReservedAnswerTokens != breakdown.ReservedAnswerTokens
            || precomputed.ReservedReasoningTokens != 0
            || precomputed.InputTokens != breakdown.InputTokens
            || precomputed.TotalTokens != breakdown.TotalTokens
            || precomputed.SafetyMarginTokens != breakdown.SafetyMarginTokens
            || precomputed.OverallClassification != breakdown.OverallClassification
            || !string.Equals(precomputed.PayloadFingerprint, breakdown.PayloadFingerprint, StringComparison.Ordinal))
        {
            return ClosedCampaignAdmissionError();
        }

        RecordEstimatedInput(breakdown, context);

        Result admission = CheckAdmission(breakdown, context);

        if (admission.IsFailure)
        {
            return admission.Error;
        }

        string modelCallId = Guid.NewGuid().ToString("N");

        try
        {
            await beforeSend(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Nothing mutable from a caller crosses the send fence. These SDK objects are
            // constructed from the exact immutable payload whose disclosure was acknowledged.
            List<ChatMessage> messages = [new(ChatRole.System, payload.SystemPrompt), new(ChatRole.User, payload.UserPrompt)];
            using JsonDocument schema = JsonDocument.Parse(CampaignMaintenancePayload.SchemaJson);
            ChatOptions options = new()
            {
                MaxOutputTokens = CampaignMaintenancePayload.MaxOutputTokens,
                Tools = [],
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(),
                    CampaignMaintenancePayload.SchemaName, CampaignMaintenancePayload.SchemaDescription),
            };

            SubagentExecutionAmbient.Tracker?.BeginModelCall();

            ChatResponse response = await chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            UsageDetails? usage = response.Usage;

            breakdown = ReconcileAndRecord(breakdown, usage, context)!;
            RecordPromptCacheMetrics(context, ModelCallPurpose.MainInference, usage);
            RecordDelegatedUsage(context, usage);

            return new CampaignMaintenanceCallResult(modelCallId, ReadClosedCampaignText(response), usage,
                response.FinishReason?.ToString(), breakdown);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogError("Model call {ModelCallId} failed for purpose {Purpose}; exception type {ExceptionType}.",
                modelCallId, ModelCallPurpose.MainInference, exception.GetType().FullName);

            return new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);
        }
    }

    private static Error ClosedCampaignAdmissionError() =>
        new(ErrorCodes.Hub.ContextBudgetExceeded,
            "The Campaign provider payload changed after context admission; refusing the stale breakdown.");

    private static string ReadClosedCampaignText(ChatResponse response)
    {
        ChatMessage[] messages = response.Messages switch
        {
            List<ChatMessage> concrete => concrete.ToArray(),
            ChatMessage[] array => (ChatMessage[])array.Clone(),
            _ => [],
        };
        StringBuilder text = new();

        foreach (ChatMessage message in messages)
        {
            if (message is null)
            {
                return string.Empty;
            }

            AIContent[]? contents = message.Contents switch
            {
                List<AIContent> concrete => concrete.ToArray(),
                AIContent[] array => (AIContent[])array.Clone(),
                _ => null,
            };

            if (contents is null)
            {
                return string.Empty;
            }

            foreach (AIContent content in contents)
            {
                if (content is TextContent part)
                {
                    _ = text.Append(part.Text);
                }
            }
        }

        // An unsupported collection yields no publishable summary, while the successful call's
        // usage survives for billing. No custom collection member is invoked to inspect it.
        return text.ToString();
    }
}
