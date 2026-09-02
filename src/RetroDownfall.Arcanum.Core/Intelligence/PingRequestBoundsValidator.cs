using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public static class PingRequestBoundsValidator
{

    public static Result Validate(PingRequest request)
    {

        IntelligenceSettings intelligence = ArcanumRuntimeDefaults.Intelligence;

        SessionSettings sessions = ArcanumRuntimeDefaults.Sessions;

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

