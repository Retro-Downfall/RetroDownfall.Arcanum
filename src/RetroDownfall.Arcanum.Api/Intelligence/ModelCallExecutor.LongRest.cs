using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelCallExecutor
{
    /// <summary>The reasoning control of an adjudication request to a model that declares reasoning: the typed effort None.</summary>
    private static readonly ReasoningOptions AdjudicationReasoningOff = new() { Effort = ReasoningEffort.None };

    public async Task<Result<LongRestProviderResult>> ExecuteLongRestChatAsync(IChatClient chatClient, LongRestProviderPayload payload,
        ITurnBudget budget, Func<CancellationToken, Task> beforeSend, Func<LongRestProviderUsage, Task> recordPaidUsage,
        LongRestProviderAdmission admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        ArgumentNullException.ThrowIfNull(budget);

        ArgumentNullException.ThrowIfNull(beforeSend);

        ArgumentNullException.ThrowIfNull(recordPaidUsage);

        if (_tokenEstimator is null || payload.Kind is not (LongRestProviderPayloadKind.Summary or LongRestProviderPayloadKind.Adjudication)
            || admission is not LongRestProviderAdmission.Chat chat || chat.Context.PrecomputedBreakdown is not { } admitted
            || chat.Context.ReservedAnswerTokens != 2048 || chat.Context.ReservedReasoningTokens != 0 || chat.Context.PromptCachePlan is not null)
        {
            return ClosedLongRestAdmissionError();
        }

        Result<LongRestChatRequest> rendered = LongRestProviderCallFreezer.RenderChat(payload);

        if (rendered.IsFailure)
        {
            return rendered.Error;
        }

        ContextTokenBreakdown actual = _tokenEstimator.EstimateLongRestChat(chat.Context.Provider, chat.Context.Model, payload);

        if (!SameLongRestAdmission(admitted, actual) || CheckAdmission(actual, chat.Context).IsFailure)
        {
            return ClosedLongRestAdmissionError();
        }

        // True only while the SDK call itself is in flight: a failure there is typed; any other failure stays generic.
        bool dispatched = false;

        try
        {
            await beforeSend(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            LongRestChatRequest request = rendered.Value;

            // SDK objects are created only after the live physical-send fence.
            List<ChatMessage> messages = [new(ChatRole.System, request.SystemPrompt), new(ChatRole.User, request.UserPrompt)];

            using JsonDocument schema = JsonDocument.Parse(request.SchemaJson);

            ChatOptions options = new()
            {
                ModelId = chat.Context.Model,
                MaxOutputTokens = request.MaxOutputTokens,
                Tools = [],
                ResponseFormat = ChatResponseFormat.ForJsonSchema(schema.RootElement.Clone(), request.SchemaName, request.SchemaDescription),
            };

            // A model that declares reasoning answers an adjudication with none (the digest the attempt was prepared under binds this).
            if (LongRestProviderCallFreezer.AdjudicationReasoningOff(payload.Kind, chat.Context.Provider, chat.Context.Model))
            {
                options.Reasoning = AdjudicationReasoningOff;
            }

            dispatched = true;

            ChatResponse response = await chatClient.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

            dispatched = false;

            LongRestProviderUsage usage = LongRestUsage(response.Usage);

            await recordPaidUsage(usage).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            _ = ReconcileAndRecord(actual, response.Usage, chat.Context);

            return ParseLongRestChat(payload, ReadLongRestText(response), usage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            int? status = null;

            Error failure = dispatched ? LongRestProviderFailure.Classify(exception, out status)
                : new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);

            _logger?.LogWarning("Long Rest call failed: {Code}; provider {Provider}, model {Model}, HTTP {HttpStatus}, operation {OperationId} ({ExceptionType}). {Remedy}",
                failure.Code, LongRestProviderFailure.Label(chat.Context.Provider.Name), LongRestProviderFailure.Label(chat.Context.Model), status,
                payload.Work.OperationId, exception.GetType().Name, dispatched ? failure.Message : "The local dispatch or accounting step failed.");

            return failure;
        }
    }

    public async Task<Result<LongRestProviderResult>> ExecuteLongRestEmbeddingAsync(IEmbeddingGenerator<string, Embedding<float>> embeddingClient,
        LongRestProviderPayload payload, ITurnBudget budget, Func<CancellationToken, Task> beforeSend,
        Func<LongRestProviderUsage, Task> recordPaidUsage, LongRestProviderAdmission admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embeddingClient);

        ArgumentNullException.ThrowIfNull(budget);

        ArgumentNullException.ThrowIfNull(beforeSend);

        ArgumentNullException.ThrowIfNull(recordPaidUsage);

        if (_tokenEstimator is null || payload.Kind != LongRestProviderPayloadKind.Embedding || admission is not LongRestProviderAdmission.Embedding embedding)
        {
            return ClosedLongRestAdmissionError();
        }

        Result<RetroDownfall.Arcanum.Core.Covenant.CovenantDigest> digest = LongRestEmbeddingPayloadHash.Create(embedding.Provider.Name,
            embedding.Model, embedding.Dimensions, payload.EmbeddingText!);

        int actual = _tokenEstimator.EstimateLongRestEmbedding(embedding.Provider, embedding.Model, payload).TokenCount;

        if (digest.IsFailure || digest.Value != embedding.InputFingerprint || actual != embedding.ReservedInputTokens || actual <= 0
            || !EmbeddingFitsWindow(embedding.Provider, actual))
        {
            return ClosedLongRestAdmissionError();
        }

        bool dispatched = false;

        try
        {
            await beforeSend(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            string[] input = [payload.EmbeddingText!];

            EmbeddingGenerationOptions options = new() { ModelId = embedding.Model, Dimensions = embedding.Dimensions };

            // Recompute from the actual sole SDK input; no Weave truncation or synthetic chat context.
            if (LongRestEmbeddingPayloadHash.Create(embedding.Provider.Name, options.ModelId, options.Dimensions!.Value, input[0]).Value != digest.Value)
            {
                return ClosedLongRestAdmissionError();
            }

            dispatched = true;

            GeneratedEmbeddings<Embedding<float>> response = await embeddingClient.GenerateAsync(input, options, cancellationToken).ConfigureAwait(false);

            dispatched = false;

            LongRestProviderUsage usage = LongRestUsage(response.Usage);

            await recordPaidUsage(usage).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (response.Count != 1 || response[0].Vector.Length != embedding.Dimensions)
            {
                return InvalidLongRestOutput(payload.Kind, usage);
            }

            ImmutableArray<float> vector = [.. response[0].Vector.ToArray()];

            return vector.All(float.IsFinite)
                ? new LongRestProviderResult(payload.Kind, LongRestProviderResultDisposition.Valid, null, null, vector, usage, null)
                : InvalidLongRestOutput(payload.Kind, usage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            int? status = null;

            Error failure = dispatched ? LongRestProviderFailure.Classify(exception, out status)
                : new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);

            _logger?.LogWarning("Long Rest call failed: {Code}; provider {Provider}, model {Model}, HTTP {HttpStatus}, operation {OperationId} ({ExceptionType}). {Remedy}",
                failure.Code, LongRestProviderFailure.Label(embedding.Provider.Name), LongRestProviderFailure.Label(embedding.Model), status,
                payload.Work.OperationId, exception.GetType().Name, dispatched ? failure.Message : "The local dispatch or accounting step failed.");

            return failure;
        }
    }

    /// <summary>
    /// The local context-window gate for one admitted Long Rest call. The maintenance runs it before it prepares an attempt, so a
    /// refusal leaves no provider-effect row and no accounting run; the executor runs the same rule again as its last gate before
    /// any SDK object exists. It reads only the admission's own estimate and window and never touches the network.
    /// </summary>
    internal static Result CheckLongRestWindow(LongRestProviderAdmission admission)
    {
        switch (admission)
        {
            case LongRestProviderAdmission.Chat { Context.PrecomputedBreakdown: { } breakdown } chat:
                Result window = CheckAdmission(breakdown, chat.Context);

                return window.IsSuccess ? window : Result.Failure(new Error(ErrorCodes.LongRest.ModelContextTooSmall,
                    window.Error.Message + " Raise the provider's contextWindowLimit or choose a model with a larger window."));

            case LongRestProviderAdmission.Embedding embedding when embedding.ReservedInputTokens > 0:
                if (EmbeddingFitsWindow(embedding.Provider, embedding.ReservedInputTokens)) return Result.Success();

                // The summary request states the recipe's summary bound, which always fits this window, so a summary that does not fit is
                // one the model ignored the bound on. This gate is the last guard for it, and the case is counted.
                ArcanumMetrics.LongRestEmbeddingWindowRefusedTotal.Add(1);

                return Result.Failure(new Error(ErrorCodes.LongRest.ModelContextTooSmall,
                    "A Long Rest summary needs " + embedding.ReservedInputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " embedding tokens but the embedding provider's window is " + embedding.Provider.ContextWindowLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ". Raise the provider's contextWindowLimit or choose an embedding provider with a larger window."));

            default:
                return Result.Failure(new Error(ErrorCodes.LongRest.InvalidAdmission, "The exact Long Rest provider admission is invalid."));
        }
    }

    private static bool EmbeddingFitsWindow(ProviderSettings provider, int tokens) => provider.ContextWindowLimit is not > 0 || tokens <= provider.ContextWindowLimit;

    private static bool SameLongRestAdmission(ContextTokenBreakdown admitted, ContextTokenBreakdown actual) =>
        admitted.Provider == actual.Provider && admitted.Model == actual.Model && admitted.Profile == actual.Profile
        && admitted.InputTokens == actual.InputTokens && admitted.TotalTokens == actual.TotalTokens
        && admitted.ReservedTokens == actual.ReservedTokens && admitted.ReservedAnswerTokens == actual.ReservedAnswerTokens
        && admitted.ReservedReasoningTokens == actual.ReservedReasoningTokens && admitted.SafetyMarginTokens == actual.SafetyMarginTokens
        && admitted.OverallClassification == actual.OverallClassification && admitted.PayloadFingerprint == actual.PayloadFingerprint;

    private static LongRestProviderUsage LongRestUsage(UsageDetails? usage) => new(usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0,
        usage?.TotalTokenCount ?? ((usage?.InputTokenCount ?? 0) + (usage?.OutputTokenCount ?? 0)), usage?.CachedInputTokenCount ?? 0, usage?.ReasoningTokenCount ?? 0);

    private static Error ClosedLongRestAdmissionError() => new(ErrorCodes.LongRest.InvalidAdmission, "The exact Long Rest provider admission is invalid or stale.");

    private static LongRestProviderResult InvalidLongRestOutput(LongRestProviderPayloadKind kind, LongRestProviderUsage usage) =>
        new(kind, LongRestProviderResultDisposition.Invalid, null, null, [], usage, ErrorCodes.LongRest.InvalidOutput);

    private static string ReadLongRestText(ChatResponse response)
    {
        ChatMessage[] messages = response.Messages switch
        {
            List<ChatMessage> list => list.ToArray(),
            ChatMessage[] array => (ChatMessage[])array.Clone(),
            _ => [],
        };

        StringBuilder text = new();

        foreach (ChatMessage message in messages)
        {
            AIContent[] contents = message.Contents switch
            {
                List<AIContent> list => list.ToArray(),
                AIContent[] array => (AIContent[])array.Clone(),
                _ => [],
            };

            foreach (AIContent content in contents)
            {
                if (content is TextContent part)
                {
                    if ((long)text.Length + part.Text.Length > 65536)
                    {
                        return string.Empty;
                    }

                    text.Append(part.Text);
                }
            }
        }

        return text.ToString();
    }

    private static LongRestProviderResult ParseLongRestChat(LongRestProviderPayload payload, string text, LongRestProviderUsage usage)
    {
        // An adjudication is member verdicts, decided as a whole by the one pure reader; the model never names a survivor or a supersession.
        if (payload.Kind == LongRestProviderPayloadKind.Adjudication)
        {
            LongRestAdjudicationDecision? decision = LongRestMemberVerdict.Decide(text, payload.Inputs);

            return decision is null
                ? InvalidLongRestOutput(payload.Kind, usage)
                : new(payload.Kind, LongRestProviderResultDisposition.Valid, null, decision, [], usage, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidLongRestOutput(payload.Kind, usage);
            }

            JsonProperty[] properties = [.. root.EnumerateObject()];

            if (properties.Length != 1 || properties[0].Name != "summary" || properties[0].Value.ValueKind != JsonValueKind.String)
            {
                return InvalidLongRestOutput(payload.Kind, usage);
            }

            string summary = properties[0].Value.GetString()!;

            return !string.IsNullOrWhiteSpace(summary) && new UTF8Encoding(false, true).GetByteCount(summary) <= LongRestDiscoveryLimits.SummaryUtf8Bytes
                ? new(payload.Kind, LongRestProviderResultDisposition.Valid, summary, null, [], usage, null)
                : InvalidLongRestOutput(payload.Kind, usage);
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException or InvalidOperationException)
        {
            return InvalidLongRestOutput(payload.Kind, usage);
        }
    }
}
