using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public static class PingRequestBoundsValidator
{
    public static Result Validate(PingRequest request)
    {
        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;

        SessionSettings sessions = ArcanumRuntimeDefaults.Sessions;

        Result inferenceOptions = ValidateInferenceOptions(request);

        if (inferenceOptions.IsFailure)
        {
            return inferenceOptions;
        }

        Result reasoningOptions = ReasoningRequestValidator.Validate(request.Reasoning);

        if (reasoningOptions.IsFailure)
        {
            return reasoningOptions;
        }

        int maxPromptChars = ArcanumSettingClamps.MaxPingPromptChars(intelligence.MaxPingPromptChars);

        if (!string.IsNullOrEmpty(request.Prompt) && request.Prompt.Length > maxPromptChars)
        {
            return Result.Failure(new Error(
                "Validation.PromptTooLong",
                $"Prompt exceeds the maximum length ({maxPromptChars} characters)."));
        }

        // W3.6: AdditionalSystemPrompt is injected verbatim into the system prompt by
        // SystemPromptBuilder; bound it like the prompt so it cannot grow the context unbounded.
        if (!string.IsNullOrEmpty(request.AdditionalSystemPrompt) && request.AdditionalSystemPrompt.Length > maxPromptChars)
        {
            return Result.Failure(new Error(
                "Validation.AdditionalSystemPromptTooLong",
                $"AdditionalSystemPrompt exceeds the maximum length ({maxPromptChars} characters)."));
        }

        List<CoreChatMessage>? stateless = request.StatelessMessages;

        if (stateless is { Count: > 0 })
        {
            int maxMessages = ArcanumSettingClamps.MaxStatelessMessages(intelligence.MaxStatelessMessages);

            if (stateless.Count > maxMessages)
            {
                return Result.Failure(new Error(
                    "Validation.TooManyStatelessMessages",
                    $"StatelessMessages exceeds the maximum count ({maxMessages})."));
            }

            int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(sessions.MaxEntryContentBytes);

            int maxContentParts = ArcanumSettingClamps.MaxContentPartsPerMessage(intelligence.MaxContentPartsPerMessage);

            for (int i = 0; i < stateless.Count; i++)
            {
                CoreChatMessage message = stateless[i];

                // W6.5: bound multimodal content-part count on the native stateless path too (the
                // /v1 surface already rejects this pre-mapping); a huge content[] can no longer enter
                // via /intelligence/ping(-stream).
                if (message.ContentParts is { Count: > 0 } messageParts && messageParts.Count > maxContentParts)
                {
                    return Result.Failure(new Error(
                        "Validation.TooManyContentParts",
                        $"StatelessMessages[{i}] exceeds the maximum content parts ({maxContentParts})."));
                }

                // W3.6: measure the full UTF-8 byte size against the byte-named budget (was a UTF-16
                // char count, which let multibyte content run ~3-4x over) and include tool-call
                // arguments + multimodal content parts so an empty-Content message with a large
                // ToolCalls/ContentParts payload can no longer bypass the cap.
                long messageBytes = MeasureMessageBytes(message);

                if (messageBytes > maxEntryBytes)
                {
                    return Result.Failure(new Error(
                        "Validation.StatelessMessageTooLong",
                        $"StatelessMessages[{i}] content exceeds the maximum size ({maxEntryBytes} bytes, UTF-8)."));
                }
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// <c>Content</c> and <c>ContentParts</c> are two representations of one payload and the mapper
    /// sends exactly one of them, so they are charged as the larger of the two rather than summed.
    /// Summing billed a caller twice for one body, which refused a structured request where the
    /// byte-identical JSON-string form of the same message passed.
    /// </summary>
    /// <remarks>
    /// A max, not a "parts win" skip: <c>MapStatelessMessageToMeAi</c> branches on role before it looks
    /// at <c>ContentParts</c>, so for <c>role = tool</c> and assistant-with-tool-calls it is
    /// <c>Content</c> that reaches the provider. Ignoring it there would let a ten-byte part stand in
    /// for a multi-megabyte message. Tool-call arguments are a genuinely separate payload and still add.
    /// </remarks>
    private static long MeasureMessageBytes(CoreChatMessage message)
    {
        long partBytes = 0;

        if (message.ContentParts is { Count: > 0 } parts)
        {
            foreach (CoreContentPart part in parts)
            {
                partBytes += Utf8Bytes(part.Text) + Utf8Bytes(part.ImageUrl);
            }
        }

        long bytes = Math.Max(Utf8Bytes(message.Content), partBytes);

        if (message.ToolCalls is { Count: > 0 } toolCalls)
        {
            foreach (CoreToolCall toolCall in toolCalls)
            {
                bytes += Utf8Bytes(toolCall.Name) + Utf8Bytes(toolCall.ArgumentsJson);
            }
        }

        return bytes;
    }

    private static long Utf8Bytes(string? value) =>
        string.IsNullOrEmpty(value) ? 0L : Encoding.UTF8.GetByteCount(value);

    private static Result ValidateInferenceOptions(PingRequest request)
    {
        if (InvalidFiniteRange(request.Temperature, 0f, 2f))
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidTemperature, "temperature must be a finite number from 0 through 2."));
        }

        if (InvalidFiniteRange(request.TopP, 0f, 1f))
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidTopP, "topP must be a finite number from 0 through 1."));
        }

        if (InvalidFiniteRange(request.PresencePenalty, -2f, 2f))
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidPresencePenalty, "presencePenalty must be a finite number from -2 through 2."));
        }

        if (InvalidFiniteRange(request.FrequencyPenalty, -2f, 2f))
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidFrequencyPenalty, "frequencyPenalty must be a finite number from -2 through 2."));
        }

        if (request.MaxOutputTokens is <= 0)
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidMaxOutputTokens, "maxOutputTokens must be a positive integer."));
        }

        if (request.ResponseFormat is not { } rawFormat)
        {
            return Result.Success();
        }

        string format = rawFormat.Trim();

        if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "json_object", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        if (!string.Equals(format, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidResponseFormat, "responseFormat must be text, json_object, or json_schema."));
        }

        if (request.ResponseFormatJsonSchema is not { ValueKind: JsonValueKind.Object } schema)
        {
            return InvalidResponseSchema();
        }

        if (schema.TryGetProperty("schema", out JsonElement nestedSchema))
        {
            if (nestedSchema.ValueKind != JsonValueKind.Object
                || (schema.TryGetProperty("strict", out JsonElement strict) && strict.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                || (schema.TryGetProperty("name", out JsonElement name) && (name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))))
            {
                return InvalidResponseSchema();
            }

            schema = nestedSchema;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(schema.GetRawText());

            Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(document, ArcanumRuntimeDefaults.StructuredOutput.SchemaMaxDepth);

            return parsed.IsFailure
                ? Result.Failure(new Error(ErrorCodes.Validation.InvalidResponseSchema,
                    $"responseFormatJsonSchema is invalid: {parsed.Error.Message}."))
                : Result.Success();
        }
        catch (JsonException)
        {
            return Result.Failure(new Error(ErrorCodes.Validation.InvalidResponseSchema,
                "responseFormatJsonSchema exceeds the supported JSON nesting depth."));
        }
    }

    private static bool InvalidFiniteRange(float? value, float minimum, float maximum) =>
        value is { } number && (!float.IsFinite(number) || number < minimum || number > maximum);

    private static Result InvalidResponseSchema() => Result.Failure(new Error(
        ErrorCodes.Validation.InvalidResponseSchema,
        "responseFormatJsonSchema must contain a JSON object schema when responseFormat is json_schema; supply it directly or in a schema wrapper with an optional nonempty name and Boolean strict value."));

    public static Result ValidateOpenApiMessageCount(int messageCount)
    {
        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;

        int maxMessages = ArcanumSettingClamps.MaxOpenApiMessages(intelligence.MaxOpenApiMessages);

        if (messageCount > maxMessages)
        {
            return Result.Failure(new Error(
                "Validation.TooManyMessages",
                $"messages[] exceeds the maximum count ({maxMessages})."));
        }

        return Result.Success();
    }
}

