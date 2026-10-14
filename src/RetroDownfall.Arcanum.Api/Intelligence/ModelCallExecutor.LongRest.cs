using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed partial class ModelCallExecutor
{
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
            _logger?.LogWarning("Long Rest provider call failed ({ExceptionType}).", exception.GetType().FullName);

            return dispatched ? LongRestProviderFailure.Classify(exception) : new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);
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
            || embedding.Provider.ContextWindowLimit is > 0 && actual > embedding.Provider.ContextWindowLimit)
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
            _logger?.LogWarning("Long Rest embedding call failed ({ExceptionType}).", exception.GetType().FullName);

            return dispatched ? LongRestProviderFailure.Classify(exception) : new Error(ErrorCodes.Hub.Error, PublicInferenceErrorMessages.NativeGenericFailure);
        }
    }

    private static bool SameLongRestAdmission(ContextTokenBreakdown admitted, ContextTokenBreakdown actual) =>
        admitted.Provider == actual.Provider && admitted.Model == actual.Model && admitted.Profile == actual.Profile
        && admitted.InputTokens == actual.InputTokens && admitted.TotalTokens == actual.TotalTokens
        && admitted.ReservedTokens == actual.ReservedTokens && admitted.ReservedAnswerTokens == actual.ReservedAnswerTokens
        && admitted.ReservedReasoningTokens == actual.ReservedReasoningTokens && admitted.SafetyMarginTokens == actual.SafetyMarginTokens
        && admitted.OverallClassification == actual.OverallClassification && admitted.PayloadFingerprint == actual.PayloadFingerprint;

    private static LongRestProviderUsage LongRestUsage(UsageDetails? usage) => new(usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0,
        usage?.TotalTokenCount ?? ((usage?.InputTokenCount ?? 0) + (usage?.OutputTokenCount ?? 0)), usage?.CachedInputTokenCount ?? 0, usage?.ReasoningTokenCount ?? 0);

    private static Error ClosedLongRestAdmissionError() => new(ErrorCodes.Hub.ContextBudgetExceeded, "The exact Long Rest provider admission is invalid or stale.");

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
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);

            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return InvalidLongRestOutput(payload.Kind, usage);
            }

            JsonProperty[] properties = [.. root.EnumerateObject()];

            if (payload.Kind == LongRestProviderPayloadKind.Summary)
            {
                if (properties.Length != 1 || properties[0].Name != "summary" || properties[0].Value.ValueKind != JsonValueKind.String)
                {
                    return InvalidLongRestOutput(payload.Kind, usage);
                }

                string summary = properties[0].Value.GetString()!;

                return !string.IsNullOrWhiteSpace(summary) && new UTF8Encoding(false, true).GetByteCount(summary) <= LongRestDiscoveryLimits.SummaryUtf8Bytes
                    ? new(payload.Kind, LongRestProviderResultDisposition.Valid, summary, null, [], usage, null)
                    : InvalidLongRestOutput(payload.Kind, usage);
            }

            if (properties.Length != 2 || properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 2
                || !root.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("survivorVersionId", out JsonElement survivor) || survivor.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            {
                return InvalidLongRestOutput(payload.Kind, usage);
            }

            LongRestAdjudicationKind? decision = kind.GetString() switch
            {
                "EquivalentObservations" => LongRestAdjudicationKind.EquivalentObservations,
                "Supersession" => LongRestAdjudicationKind.Supersession,
                "NoChange" => LongRestAdjudicationKind.NoChange,
                _ => null,
            };

            string? version = survivor.GetString();

            bool valid = decision is not null && (decision == LongRestAdjudicationKind.Supersession
                ? version is not null && payload.Inputs.Any(input => input.Snapshot.VersionId == version) : version is null);

            return valid ? new(payload.Kind, LongRestProviderResultDisposition.Valid, null, new(decision!.Value, version), [], usage, null)
                : InvalidLongRestOutput(payload.Kind, usage);
        }
        catch (Exception exception) when (exception is JsonException or EncoderFallbackException or InvalidOperationException)
        {
            return InvalidLongRestOutput(payload.Kind, usage);
        }
    }
}
