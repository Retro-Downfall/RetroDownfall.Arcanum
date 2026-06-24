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

            for (int i = 0; i < stateless.Count; i++)
            {

                CoreChatMessage message = stateless[i];

                int contentChars = message.Content?.Length ?? 0;

                if (contentChars > maxEntryBytes)
                {

                    return Result.Failure(new Error(
                        "Validation.StatelessMessageTooLong",
                        $"StatelessMessages[{i}].content exceeds the maximum length ({maxEntryBytes} characters)."));

                }

            }

        }

        return Result.Success();

    }

    public static Result ValidateOpenApiMessageCount(int messageCount, ArcanumSettings settings)
    {

        int maxMessages = ArcanumSettingClamps.MaxOpenApiMessages(settings.Intelligence.MaxOpenApiMessages);

        if (messageCount > maxMessages)
        {

            return Result.Failure(new Error(
                "Validation.TooManyMessages",
                $"messages[] exceeds the maximum count ({maxMessages})."));

        }

        return Result.Success();

    }

}

