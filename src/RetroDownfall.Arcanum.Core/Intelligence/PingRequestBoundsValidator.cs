using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

public static class PingRequestBoundsValidator
{

    public static Result Validate(PingRequest request, ArcanumSettings settings)
    {

        IntelligenceSettings intelligence = settings.Intelligence ?? new IntelligenceSettings();

        SessionSettings sessions = settings.Sessions ?? new SessionSettings();

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

    private static long MeasureMessageBytes(CoreChatMessage message)
    {

        long bytes = Utf8Bytes(message.Content);

        if (message.ToolCalls is { Count: > 0 } toolCalls)
        {

            foreach (CoreToolCall toolCall in toolCalls)
            {

                bytes += Utf8Bytes(toolCall.Name) + Utf8Bytes(toolCall.ArgumentsJson);

            }

        }

        if (message.ContentParts is { Count: > 0 } parts)
        {

            foreach (CoreContentPart part in parts)
            {

                bytes += Utf8Bytes(part.Text) + Utf8Bytes(part.ImageUrl);

            }

        }

        return bytes;

    }

    private static long Utf8Bytes(string? value) =>
        string.IsNullOrEmpty(value) ? 0L : Encoding.UTF8.GetByteCount(value);

    public static Result ValidateOpenApiMessageCount(int messageCount, ArcanumSettings settings)
    {

        IntelligenceSettings intelligence = settings.Intelligence ?? new IntelligenceSettings();

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

