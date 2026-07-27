using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Default provider-call boundary. When supplied a <see cref="ModelCallContext"/>, it creates the
/// single authoritative pre-call breakdown, enforces admission before provider I/O, and reconciles
/// provider-reported input usage without replacing the estimate.
/// </summary>
public sealed class ModelCallExecutor : IModelCallExecutor
{
    private readonly IModelTokenEstimator? _tokenEstimator;

    private readonly IOptionsMonitor<ArcanumSettings>? _settings;

    private readonly bool _allowUnaccountedCompatibilityCalls;

    public ModelCallExecutor(IModelTokenEstimator tokenEstimator)
        : this(tokenEstimator, settings: null)
    {
    }

    public ModelCallExecutor(
        IModelTokenEstimator tokenEstimator,
        IOptionsMonitor<ArcanumSettings>? settings)
    {
        _tokenEstimator = tokenEstimator ?? throw new ArgumentNullException(nameof(tokenEstimator));
        _settings = settings;
    }

    internal ModelCallExecutor()
    {
        _allowUnaccountedCompatibilityCalls = true;
    }

    public async Task<ModelCallOutcome> ExecuteBufferedAsync(
        IChatClient chatClient,
        IList<ChatMessage> messages,
        ChatOptions options,
        ITurnBudget budget,
        ModelCallPurpose purpose,
        CancellationToken cancellationToken,
        ModelCallContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budget);

        if (ValidatePrecomputedBreakdown(messages, options, context) is { } staleError)
        {
            return ModelCallOutcome.Failed(new ModelCallFailure(
                purpose,
                string.Empty,
                staleError,
                Cause: null));
        }

        if (ValidatePromptCachePlan(messages, context) is { } cachePlanError)
        {
            return ModelCallOutcome.Failed(new ModelCallFailure(
                purpose,
                string.Empty,
                cachePlanError,
                Cause: null));
        }

        ContextTokenBreakdown? breakdown = EstimateAndRecord(messages, options, context);
        Result admission = CheckAdmission(breakdown, context);
        if (admission.IsFailure)
        {
            return ModelCallOutcome.Failed(new ModelCallFailure(
                purpose,
                string.Empty,
                admission.Error,
                Cause: null));
        }

        string modelCallId = Guid.NewGuid().ToString("N");
        ProviderCallPayload providerPayload = CreateProviderCallPayload(
            messages,
            options,
            context);

        try
        {
            ChatResponse response = await chatClient
                .GetResponseAsync(
                    providerPayload.Messages,
                    providerPayload.Options,
                    cancellationToken)
                .ConfigureAwait(false);
            (ReasoningOutputMode? requestedOutput, ReasoningOutputMode effectiveOutput) =
                ResolveReasoningOutput(options, purpose);
            ModelCallReasoningResult reasoning = ExtractReasoning(
                response,
                requestedOutput,
                effectiveOutput);
            breakdown = ReconcileAndRecord(breakdown, response.Usage, context);
            RecordPromptCacheMetrics(context, purpose, response.Usage);

            return ModelCallOutcome.Success(
                new ModelCallResult(
                    purpose,
                    modelCallId,
                    response,
                    response.Usage,
                    reasoning,
                    breakdown));
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
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ModelCallContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(budget);

        if (ValidatePrecomputedBreakdown(messages, options, context) is { } staleError)
        {
            yield return new ModelCallFailureUpdate(
                purpose,
                string.Empty,
                staleError);
            yield break;
        }

        if (ValidatePromptCachePlan(messages, context) is { } cachePlanError)
        {
            yield return new ModelCallFailureUpdate(
                purpose,
                string.Empty,
                cachePlanError);
            yield break;
        }

        ContextTokenBreakdown? breakdown = EstimateAndRecord(messages, options, context);
        Result admission = CheckAdmission(breakdown, context);
        if (admission.IsFailure)
        {
            yield return new ModelCallFailureUpdate(
                purpose,
                string.Empty,
                admission.Error);
            yield break;
        }

        string modelCallId = Guid.NewGuid().ToString("N");
        ProviderCallPayload providerPayload = CreateProviderCallPayload(
            messages,
            options,
            context);
        (ReasoningOutputMode? requestedOutput, ReasoningOutputMode effectiveOutput) =
            ResolveReasoningOutput(providerPayload.Options, purpose);

        if (breakdown is not null)
        {
            yield return new ModelCallContextUpdate(purpose, modelCallId, breakdown);
        }

        UsageDetails? finalUsage = null;
        await foreach (ChatResponseUpdate update in chatClient
            .GetStreamingResponseAsync(
                providerPayload.Messages,
                providerPayload.Options,
                cancellationToken)
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
                            if (usageContent.Details is not null)
                            {
                                finalUsage = usageContent.Details;
                            }
                            ContextTokenBreakdown? reconciled = Reconcile(
                                breakdown,
                                usageContent.Details);
                            bool reconciliationChanged = !ReferenceEquals(reconciled, breakdown);
                            breakdown = reconciled;
                            if (reconciliationChanged && breakdown is not null)
                            {
                                yield return new ModelCallContextUpdate(purpose, modelCallId, breakdown);
                            }

                            yield return new ModelCallUsageUpdate(
                                purpose,
                                modelCallId,
                                usageContent.Details);
                            break;
                    }
                }
            }

            // Semantic updates are emitted first so provider commitment is recorded before any raw
            // response update can be projected to a client.
            yield return new ModelCallResponseUpdate(purpose, modelCallId, update);
        }

        RecordReconciliationMetrics(breakdown, finalUsage, context);
        RecordPromptCacheMetrics(context, purpose, finalUsage);
    }

    private void RecordPromptCacheMetrics(
        ModelCallContext? context,
        ModelCallPurpose purpose,
        UsageDetails? usage)
    {
        if (context is null)
        {
            return;
        }

        PromptCachingProfile? profile = ProviderResolver.ResolvePromptCachingProfile(
            context.Provider,
            context.Model);
        PromptCachePlan? plan = context.PromptCachePlan;
        PromptCacheEligibility eligibility = plan?.Eligibility
            ?? profile?.ControlMode switch
            {
                PromptCachingControlMode.None => PromptCacheEligibility.NonCacheable,
                PromptCachingControlMode.Explicit => PromptCacheEligibility.NonCacheable,
                _ => PromptCacheEligibility.ProviderManaged,
            };
        PromptCacheNonEligibilityReason reason = plan?.NonEligibilityReason
            ?? profile?.ControlMode switch
            {
                PromptCachingControlMode.None => PromptCacheNonEligibilityReason.DisabledByProfile,
                PromptCachingControlMode.Explicit => PromptCacheNonEligibilityReason.InvalidPlan,
                PromptCachingControlMode.ProviderManaged => PromptCacheNonEligibilityReason.ProviderManaged,
                _ => PromptCacheNonEligibilityReason.ProfileAbsent,
            };
        TagList tags = new()
        {
            { "provider", context.Provider.Name },
            { "model", context.Model },
            { "purpose", purpose.ToString() },
            { "control_mode", profile?.ControlMode.ToString() ?? "Unknown" },
            { "eligibility", eligibility.ToString() },
            { "reason", reason.ToString() },
        };

        ArcanumMetrics.PromptCacheCallsTotal.Add(1, tags);

        ModelPricingEntry? pricing =
            _settings?.CurrentValue.ResolvePricing().ResolveForModel(context.Model);

        if (pricing is not null
            && plan is { Eligibility: PromptCacheEligibility.Eligible }
            && CostCalculator.CalculatePromptCacheSavings(
                plan.EligiblePrefixTokenEstimate,
                pricing) is > 0m and var potentialSavings)
        {
            ArcanumMetrics.PromptCachePotentialSavingsUsdTotal.Add(
                (double)potentialSavings,
                tags);
        }

        bool reportsCachedInput = profile?.ReportsCachedInputUsage == true;
        long cachedTokens = Math.Max(0L, usage?.CachedInputTokenCount ?? 0L);

        if (!reportsCachedInput || cachedTokens <= 0)
        {
            return;
        }

        ArcanumMetrics.PromptCacheTokensTotal.Add(cachedTokens, tags);
        ArcanumMetrics.PromptCacheHitsTotal.Add(1, tags);

        if (pricing is not null
            && CostCalculator.CalculatePromptCacheSavings(
                cachedTokens,
                pricing) is > 0m and var actualSavings)
        {
            ArcanumMetrics.PromptCacheActualSavingsUsdTotal.Add(
                (double)actualSavings,
                tags);
        }
    }

    private static Error? ValidatePromptCachePlan(
        IList<ChatMessage> messages,
        ModelCallContext? context)
    {
        if (context?.PromptCachePlan is not { } plan
            || plan.Eligibility != PromptCacheEligibility.Eligible)
        {
            return null;
        }

        PromptCachingProfile? profile = ProviderResolver.ResolvePromptCachingProfile(
            context.Provider,
            context.Model);
        bool validTarget = string.Equals(
                plan.Provider,
                context.Provider.Name,
                StringComparison.Ordinal)
            && string.Equals(plan.Model, context.Model, StringComparison.Ordinal)
            && profile is
            {
                ControlMode: PromptCachingControlMode.Explicit,
                WireDialect: PromptCachingWireDialect.OpenAiPromptCacheRetention,
            }
            && plan.Retention == profile.Retention
            && (!profile.EmitCacheKey || !string.IsNullOrEmpty(plan.CacheKey));

        if (validTarget)
        {
            foreach (PromptCacheBoundary boundary in plan.Boundaries)
            {
                if (boundary.MessageIndex < 0
                    || boundary.MessageIndex >= messages.Count
                    || boundary.SegmentIndex < 0
                    || messages[boundary.MessageIndex].Role != ChatRole.System)
                {
                    validTarget = false;
                    break;
                }
            }
        }

        return validTarget
            ? null
            : new Error(
                ErrorCodes.Hub.ContextBudgetExceeded,
                "The prompt-cache plan does not match the selected provider payload.");
    }

    private static ProviderCallPayload CreateProviderCallPayload(
        IList<ChatMessage> messages,
        ChatOptions options,
        ModelCallContext? context)
    {
        if (context?.PromptCachePlan is not
            {
                Eligibility: PromptCacheEligibility.Eligible,
            } plan)
        {
            return new ProviderCallPayload(messages, options);
        }

        PromptCachingProfile profile = ProviderResolver.ResolvePromptCachingProfile(
                context.Provider,
                context.Model)
            ?? throw new InvalidOperationException(
                "An eligible prompt-cache plan requires a resolved provider/model profile.");
        List<ChatMessage> providerMessages = new(messages.Count);

        foreach (ChatMessage message in messages)
        {
            providerMessages.Add(message.Clone());
        }

        ChatOptions providerOptions = options.Clone();

        PromptCachingChatOptionsAdapter.Apply(providerOptions, profile, plan);

        return new ProviderCallPayload(providerMessages, providerOptions);
    }

    private ContextTokenBreakdown? EstimateAndRecord(
        IList<ChatMessage> messages,
        ChatOptions options,
        ModelCallContext? context)
    {
        if (context is null)
        {
            if (_allowUnaccountedCompatibilityCalls)
            {
                return null;
            }

            throw new InvalidOperationException(
                "A model-call context is required for provider-call admission.");
        }

        IReadOnlyList<ChatMessage> messageList = messages as IReadOnlyList<ChatMessage>
            ?? [.. messages];
        if (context.PrecomputedBreakdown is { } precomputed)
        {
            RecordEstimatedInput(precomputed, context);
            return precomputed;
        }

        if (_tokenEstimator is null)
        {
            throw new InvalidOperationException(
                "A model token estimator is required when a model-call context is supplied.");
        }

        ContextTokenBreakdown breakdown = _tokenEstimator.EstimateContext(
            new ModelTokenizationRequest(
                context.Provider,
                context.Model,
                messageList,
                options,
                context.ReservedAnswerTokens,
                context.ReservedReasoningTokens));
        RecordEstimatedInput(breakdown, context);
        return breakdown;
    }

    private Error? ValidatePrecomputedBreakdown(
        IList<ChatMessage> messages,
        ChatOptions options,
        ModelCallContext? context)
    {
        if (context?.PrecomputedBreakdown is not { } precomputed)
        {
            return null;
        }

        if (_tokenEstimator is null)
        {
            return new Error(
                ErrorCodes.Hub.ContextBudgetExceeded,
                "The precomputed context breakdown cannot be validated.");
        }

        IReadOnlyList<ChatMessage> messageList = messages as IReadOnlyList<ChatMessage>
            ?? [.. messages];
        int expectedAnswer = Math.Max(0, context.ReservedAnswerTokens);
        int expectedReasoning = Math.Max(0, context.ReservedReasoningTokens);
        int expectedReserved = ContextTokenBreakdown.SaturatingInt(
            (long)expectedAnswer + expectedReasoning);
        bool valid = string.Equals(
                precomputed.Provider,
                context.Provider.Name,
                StringComparison.Ordinal)
            && string.Equals(precomputed.Model, context.Model, StringComparison.Ordinal)
            && precomputed.ReservedTokens == expectedReserved
            && precomputed.ReservedAnswerTokens == expectedAnswer
            && precomputed.ReservedReasoningTokens == expectedReasoning
            && precomputed.Profile == _tokenEstimator.ResolveEffectiveProfile(
                context.Provider,
                context.Model)
            && precomputed.InputTokens >= 0
            && precomputed.TotalTokens == ContextTokenBreakdown.SaturatingInt(
                (long)precomputed.InputTokens + precomputed.ReservedTokens)
            && precomputed.PayloadFingerprint.Length > 0
            && string.Equals(
                precomputed.PayloadFingerprint,
                ModelCallPayloadFingerprint.Compute(messageList, options),
                StringComparison.Ordinal);

        return valid
            ? null
            : new Error(
                ErrorCodes.Hub.ContextBudgetExceeded,
                "The provider payload changed after context admission; refusing the stale breakdown.");
    }

    private static void RecordEstimatedInput(
        ContextTokenBreakdown breakdown,
        ModelCallContext context) =>
        ArcanumMetrics.EstimatedInputTokens.Record(
            breakdown.InputTokens,
            new KeyValuePair<string, object?>("provider", context.Provider.Name),
            new KeyValuePair<string, object?>("model", context.Model));

    private static Result CheckAdmission(
        ContextTokenBreakdown? breakdown,
        ModelCallContext? context)
    {
        if (breakdown is null || context is null)
        {
            return Result.Success();
        }

        Result admission = TurnContextGuards.CheckContextBudget(
            breakdown,
            context.Provider.ContextWindowLimit);
        if (admission.IsSuccess)
        {
            return admission;
        }

        ArcanumMetrics.ContextBudgetRejectionsTotal.Add(
            1,
            new KeyValuePair<string, object?>("provider", context.Provider.Name),
            new KeyValuePair<string, object?>("model", context.Model));
        return admission;
    }

    private static ContextTokenBreakdown? ReconcileAndRecord(
        ContextTokenBreakdown? breakdown,
        UsageDetails? usage,
        ModelCallContext? context)
    {
        ContextTokenBreakdown? reconciled = Reconcile(breakdown, usage);
        RecordReconciliationMetrics(reconciled, usage, context);
        return reconciled;
    }

    private static ContextTokenBreakdown? Reconcile(
        ContextTokenBreakdown? breakdown,
        UsageDetails? usage)
    {
        if (breakdown is null || usage?.InputTokenCount is not long reported)
        {
            return breakdown;
        }

        return breakdown.ReconcileProviderReportedInput(reported);
    }

    private static void RecordReconciliationMetrics(
        ContextTokenBreakdown? reconciled,
        UsageDetails? usage,
        ModelCallContext? context)
    {
        if (reconciled is null || usage?.InputTokenCount is null || context is null)
        {
            return;
        }

        KeyValuePair<string, object?> providerTag = new("provider", context.Provider.Name);
        KeyValuePair<string, object?> modelTag = new("model", context.Model);
        if (reconciled.ProviderReportedInputTokens is long reported && reported >= 0)
        {
            ArcanumMetrics.ProviderReportedInputTokens.Record(
                reported,
                providerTag,
                modelTag);
        }

        long variance = reconciled.EstimationVarianceTokens ?? 0;
        ArcanumMetrics.InputTokenEstimationVariance.Record(
            variance == long.MinValue ? long.MaxValue : Math.Abs(variance),
            providerTag,
            modelTag,
            new KeyValuePair<string, object?>(
                "direction",
                reconciled.ProviderReportedInputValid == false
                    ? "inconsistent"
                    : variance > 0
                        ? "underestimated"
                        : variance < 0
                            ? "overestimated"
                            : "exact"));
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

    private readonly record struct ProviderCallPayload(
        IList<ChatMessage> Messages,
        ChatOptions Options);
}
