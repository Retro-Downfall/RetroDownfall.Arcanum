using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelCallExecutor
{
    public async Task<Result<MemoryEvaluationArmResult>> ExecuteMemoryEvaluationAsync(
        IChatClient chatClient, MemoryEvaluationExecutionPlan plan, int pairIndex, MemoryEvaluationArm arm,
        ITurnBudget budget, Func<MemoryEvaluationSendEvidence, CancellationToken, Task> beforeSend,
        Func<MemoryEvaluationUsage, Task> recordUsage, ModelCallContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (!string.Equals(context.Model, plan.Request.Request.Model, StringComparison.Ordinal)
            || !string.Equals(context.Provider.Name, plan.Request.Request.ProviderName, StringComparison.OrdinalIgnoreCase)
            || _tokenEstimator is null || context.PrecomputedBreakdown is not { } admitted || context.PromptCachePlan is not null
            || context.ReservedAnswerTokens != plan.Request.Request.Options.MaxOutputTokens || context.ReservedReasoningTokens != 0)
            return new Error(ErrorCodes.MemoryEvaluation.InvalidAdmission, "The exact evaluation context admission is unavailable.");

        List<ChatMessage> messages = MemoryEvaluationProviderCallFreezer.Messages(plan, arm);

        ContextTokenBreakdown actual = _tokenEstimator.EstimateMemoryEvaluation(context.Provider, context.Model,
            messages[0].Text, messages[1].Text, context.ReservedAnswerTokens);

        if (actual.Provider != admitted.Provider || actual.Model != admitted.Model || actual.Profile != admitted.Profile
            || actual.InputTokens != admitted.InputTokens || actual.TotalTokens != admitted.TotalTokens
            || actual.ReservedTokens != admitted.ReservedTokens || actual.ReservedAnswerTokens != admitted.ReservedAnswerTokens
            || actual.ReservedReasoningTokens != admitted.ReservedReasoningTokens || actual.SafetyMarginTokens != admitted.SafetyMarginTokens
            || actual.OverallClassification != admitted.OverallClassification || actual.PayloadFingerprint != admitted.PayloadFingerprint)
            return new Error(ErrorCodes.MemoryEvaluation.InvalidAdmission, "The exact evaluation context admission changed.");

        Result admission = CheckAdmission(actual, context);

        if (admission.IsFailure) return admission.Error;

        ChatOptions options = MemoryEvaluationProviderCallFreezer.MapOptions(plan.Request.Request.Options, context.Model);

        ProviderCallEnvelope envelope = MemoryEvaluationProviderCallFreezer.Freeze(plan, arm, context, options, messages);

        int index = pairIndex * 2 + (plan.SendOrder[pairIndex * 2] == arm ? 0 : 1);

        MemoryEvaluationSendEvidence evidence = new(index, pairIndex, arm, envelope, envelope.Digest, plan.DestinationDigest, envelope.Sensitivity.Digest);

        await beforeSend(evidence, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();

        long start = Stopwatch.GetTimestamp();

        ChatResponse response;

        try
        {
            response = await chatClient.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        }
        catch (MemoryEvaluationRejectedResponseException rejected)
        {
            // The SDK normalizes absent primary usage and discards unsupported choices/tools.
            // Preserve raw paid measurements even when no normalized answer may be accepted.
            await recordUsage(rejected.Usage).ConfigureAwait(false);

            return new MemoryEvaluationArmResult(false, rejected.Reason, [],
                new CovenantDigest(SHA256.HashData([])), rejected.Usage,
                (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, null,
                string.Equals(rejected.Model, context.Model, StringComparison.Ordinal), rejected.Complete, rejected.UsageKnown);
        }

        UsageDetails? usage = response.Usage;

        long? input = usage?.InputTokenCount is >= 0 ? usage.InputTokenCount : null;

        long? output = usage?.OutputTokenCount is >= 0 ? usage.OutputTokenCount : null;

        long cached = usage?.CachedInputTokenCount ?? 0;

        long reasoning = usage?.ReasoningTokenCount ?? 0;

        bool known = input.HasValue && output.HasValue && cached >= 0 && reasoning >= 0 && cached <= input && reasoning <= output;

        MemoryEvaluationUsage measured = new(input, output, null, known ? cached : null, known ? reasoning : null);

        // Record paid usage before cancellation or content validation; never infer absent measurements as zero.
        await recordUsage(measured).ConfigureAwait(false);

        bool model = string.Equals(response.ModelId, context.Model, StringComparison.Ordinal);

        bool complete = response.FinishReason == ChatFinishReason.Stop;

        string? answer = ReadEvaluationText(response);

        MemoryEvaluationReason reason = !known ? MemoryEvaluationReason.UsageFailure
            : !model ? MemoryEvaluationReason.ModelMismatch
            : !complete || answer is null ? MemoryEvaluationReason.InvalidOutput
            : ct.IsCancellationRequested ? MemoryEvaluationReason.Cancelled : MemoryEvaluationReason.None;

        return new MemoryEvaluationArmResult(reason == MemoryEvaluationReason.None, reason, [],
            new CovenantDigest(SHA256.HashData(Encoding.UTF8.GetBytes(answer ?? string.Empty))), measured,
            (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds, reason == MemoryEvaluationReason.None ? answer : null,
            model, complete, known);
    }

    private static string? ReadEvaluationText(ChatResponse response)
    {
        ChatMessage[] messages = response.Messages switch
        {
            List<ChatMessage> values => values.ToArray(),
            ChatMessage[] values => (ChatMessage[])values.Clone(),
            _ => [],
        };

        if (messages.Length != 1 || messages[0].Role != ChatRole.Assistant) return null;

        AIContent[]? contents = messages[0].Contents switch
        {
            List<AIContent> values => values.ToArray(),
            AIContent[] values => (AIContent[])values.Clone(),
            _ => null,
        };

        if (contents is null) return null;

        StringBuilder answer = new();

        int bytes = 0;

        try
        {
            UTF8Encoding utf8 = new(false, true);

            foreach (AIContent content in contents)
            {
                string? text = content switch { TextContent part => part.Text, TextReasoningContent part => part.Text, _ => null };

                if (text is null) return null;

                bytes = checked(bytes + utf8.GetByteCount(text));

                if (bytes > MemoryEvaluationLimits.RetainedTextBytes) return null;

                if (content is TextContent) answer.Append(text);
            }
        }
        catch (ArgumentException) { return null; }

        return answer.Length == 0 ? null : answer.ToString();
    }
}
