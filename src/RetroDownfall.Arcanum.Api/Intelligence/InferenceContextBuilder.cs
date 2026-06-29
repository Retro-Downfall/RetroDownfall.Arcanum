using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using Microsoft.ML.Tokenizers;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

using RetroDownfall.Arcanum.Infrastructure.Workspaces;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// History load, read-time compression decision, and Microsoft.Extensions.AI message mapping shared
/// by buffered and streaming inference paths in <see cref="WizardIntelligenceProvider"/>.
/// </summary>
public sealed class InferenceContextBuilder(
    IGrimoireRepository grimoire,
    IOptionsSnapshot<ArcanumSettings> settings,
    ILogger<InferenceContextBuilder> logger,
    ManaPreflight manaPreflight,
    InferenceTokenizerResolver inferenceTokenizerResolver)
{

    public static bool HasStatelessMessages(PingRequest request) =>
        request.StatelessMessages is { Count: > 0 };

    public async Task<Session?> LoadThreadAsync(PingRequest request, CancellationToken cancellationToken)
    {

        if (HasStatelessMessages(request) || request.SessionId is not { } existingSessionId)
        {

            return null;

        }

        return await grimoire
            .GetSessionAsync(existingSessionId, cancellationToken)
            .ConfigureAwait(false);

    }

    public static List<MeAiChatMessage> BuildInitialMeAiChatMessages(
        PingRequest request,
        Session? thread,
        string newUserPrompt)
    {

        if (HasStatelessMessages(request))
        {

            return MapStatelessMessagesToMeAi(request.StatelessMessages!);

        }

        return MapGrimoireToMeAiMessages(thread, newUserPrompt);

    }

    public static void PrependDynamicSystemMessage(List<MeAiChatMessage> messages, string systemText)
    {

        if (string.IsNullOrWhiteSpace(systemText))
        {

            return;

        }

        messages.Insert(0, new MeAiChatMessage(ChatRole.System, systemText));

    }

    public (bool Compressed, List<MeAiChatMessage> Messages) TryApplyContextCompressionIfNeeded(
        PingRequest request,
        List<MeAiChatMessage> chatMessages,
        string? codexContent,
        ParsedSpell? activeSpell,
        IReadOnlyList<ParsedSpell>? dependencySpells,
        Session? thread,
        string newUserPrompt,
        ChatClientLease lease)
    {

        if (!settings.Value.Intelligence.EnableContextCompression)
        {

            return (false, chatMessages);

        }

        if (HasStatelessMessages(request) || thread is null)
        {

            return (false, chatMessages);

        }

        int minMessagesForPreflight = ArcanumSettingClamps.CompressionPreflightMinMessages(
            settings.Value.Intelligence.CompressionPreflightMinMessages);

        if (manaPreflight.ShouldSkipCompressionPreflight(chatMessages, minMessagesForPreflight))
        {

            return (false, chatMessages);

        }

        string encodingName = settings.Value.Intelligence.TokenizerEncoding ?? InferenceTokenizerResolver.DefaultEncodingName;

        Tokenizer tokenizer = inferenceTokenizerResolver.ResolveTokenizer(encodingName);

        int perMessageOverhead = ArcanumSettingClamps.PerMessageTemplateOverheadTokens(
            settings.Value.Intelligence.PerMessageTemplateOverheadTokens);

        int totalTokens = manaPreflight.CountTokens(chatMessages, tokenizer, perMessageOverhead, encodingName);

        int thresholdPct = ArcanumSettingClamps.ContextWindowCompressionThreshold(
            settings.Value.Intelligence.ContextWindowCompressionThreshold);

        int contextLimit = ArcanumSettingClamps.ContextWindowLimit(lease.Provider.ContextWindowLimit);

        long effectiveLong = (long)contextLimit * thresholdPct / 100L;

        int effectiveLimit = effectiveLong > int.MaxValue ? int.MaxValue : (int)effectiveLong;

        if (totalTokens <= effectiveLimit)
        {

            return (false, chatMessages);

        }

        if (string.IsNullOrWhiteSpace(thread.Summary) || thread.LastSummarizedMessageAt is null)
        {

            logger.LogWarning(
                "Context ({TotalTokens} tokens) exceeds compression threshold ({EffectiveLimit} tokens) but no campaign summary is available for session {SessionId}; proceeding unfiltered.",
                totalTokens,
                effectiveLimit,
                thread.Id);

            return (false, chatMessages);

        }

        List<MeAiChatMessage> rebuilt = MapFilteredGrimoireToMeAiMessages(
            thread,
            thread.LastSummarizedMessageAt.Value,
            newUserPrompt);

        string augmentedSystem = SystemPromptBuilder.Build(
            request,
            codexContent,
            activeSpell,
            request.AttachedFiles,
            thread.Summary,
            dependencySpells,
            maxResonantBytes: ArcanumSettingClamps.MaxResonantBytes(settings.Value.Spells.MaxResonantBytes));

        PrependDynamicSystemMessage(rebuilt, augmentedSystem);

        int afterTokens = manaPreflight.CountTokens(rebuilt, tokenizer, perMessageOverhead, encodingName);

        if (afterTokens > effectiveLimit)
        {

            logger.LogWarning(
                "After memory compression, context is still {AfterTokens} tokens (threshold {EffectiveLimit}) for session {SessionId}.",
                afterTokens,
                effectiveLimit,
                thread.Id);

        }

        return (true, rebuilt);

    }

    internal static List<MeAiChatMessage> MapGrimoireToMeAiMessages(Session? session, string newUserPrompt)
    {

        if (session is null)
        {

            return [new MeAiChatMessage(ChatRole.User, newUserPrompt)];

        }

        var ordered = session.Entries.ToList();

        while (ordered.Count > 0

            && ordered[^1].Role == MessageRole.Assistant

            && string.IsNullOrEmpty(ordered[^1].Content))
        {

            ordered.RemoveAt(ordered.Count - 1);

        }

        var list = new List<MeAiChatMessage>(ordered.Count + 1);

        foreach (var m in ordered)
        {

            ChatRole role = m.Role switch
            {

                MessageRole.User => ChatRole.User,

                MessageRole.Assistant => ChatRole.Assistant,

                MessageRole.System => ChatRole.System,

                _ => ChatRole.User,

            };

            list.Add(new MeAiChatMessage(role, m.Content));

        }

        list.Add(new MeAiChatMessage(ChatRole.User, newUserPrompt));

        return list;

    }

    internal static int ComputeEffectiveCompressionLimit(int contextWindowLimit, int thresholdPercent)
    {

        int contextLimit = ArcanumSettingClamps.ContextWindowLimit(contextWindowLimit);

        int thresholdPct = ArcanumSettingClamps.ContextWindowCompressionThreshold(thresholdPercent);

        long effectiveLong = (long)contextLimit * thresholdPct / 100L;

        return effectiveLong > int.MaxValue ? int.MaxValue : (int)effectiveLong;

    }

    private static List<MeAiChatMessage> MapFilteredGrimoireToMeAiMessages(
        Session session,
        DateTime watermarkExclusive,
        string newUserPrompt)
    {

        List<Entry> ordered = session.Entries
            .Where(m => m.CreatedAt.UtcDateTime > watermarkExclusive)
            .OrderBy(m => m.CreatedAt)
            .ToList();

        while (ordered.Count > 0

            && ordered[^1].Role == MessageRole.Assistant

            && string.IsNullOrEmpty(ordered[^1].Content))
        {

            ordered.RemoveAt(ordered.Count - 1);

        }

        var list = new List<MeAiChatMessage>(ordered.Count + 1);

        foreach (Entry m in ordered)
        {

            ChatRole role = m.Role switch
            {

                MessageRole.User => ChatRole.User,

                MessageRole.Assistant => ChatRole.Assistant,

                MessageRole.System => ChatRole.System,

                _ => ChatRole.User,

            };

            list.Add(new MeAiChatMessage(role, m.Content));

        }

        list.Add(new MeAiChatMessage(ChatRole.User, newUserPrompt));

        return list;

    }

    private static List<MeAiChatMessage> MapStatelessMessagesToMeAi(IReadOnlyList<CoreChatMessage> messages)
    {

        var list = new List<MeAiChatMessage>(messages.Count);

        foreach (CoreChatMessage m in messages)
        {

            list.Add(MapStatelessMessageToMeAi(m));

        }

        return list;

    }

    private static MeAiChatMessage MapStatelessMessageToMeAi(CoreChatMessage m)
    {

        ChatRole role = MapOpenAiStyleRoleToChatRole(m.Role);

        if (role == ChatRole.Tool && !string.IsNullOrEmpty(m.ToolCallId))
        {

            return new MeAiChatMessage(ChatRole.Tool, [new FunctionResultContent(m.ToolCallId, m.Content ?? string.Empty)]);

        }

        if (role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 } toolCalls)
        {

            List<AIContent> contents = new(toolCalls.Count + 1);

            if (!string.IsNullOrEmpty(m.Content))
            {

                contents.Add(new TextContent(m.Content));

            }

            foreach (CoreToolCall tc in toolCalls)
            {

                Dictionary<string, object?>? args = ParseToolCallArgumentsForAiFunction(tc.ArgumentsJson);

                contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));

            }

            return new MeAiChatMessage(ChatRole.Assistant, contents);

        }

        if (m.ContentParts is { Count: > 0 } parts)
        {

            List<AIContent> contents = new(parts.Count);

            foreach (CoreContentPart part in parts)
            {

                if (string.Equals(part.Kind, "image_url", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(part.ImageUrl)
                    && Uri.TryCreate(part.ImageUrl, UriKind.Absolute, out Uri? imageUri))
                {

                    contents.Add(new UriContent(imageUri, "image/*"));

                    continue;

                }

                string text = part.Text ?? string.Empty;

                if (text.Length > 0)
                {

                    contents.Add(new TextContent(text));

                }

            }

            if (contents.Count == 0)
            {

                contents.Add(new TextContent(string.Empty));

            }

            MeAiChatMessage built = new(role, contents);

            if (!string.IsNullOrEmpty(m.Name))
            {

                built.AuthorName = m.Name;

            }

            return built;

        }

        MeAiChatMessage textOnly = new(role, m.Content ?? string.Empty);

        if (!string.IsNullOrEmpty(m.Name))
        {

            textOnly.AuthorName = m.Name;

        }

        return textOnly;

    }

    private static Dictionary<string, object?>? ParseToolCallArgumentsForAiFunction(string? argumentsJson)
    {

        if (string.IsNullOrWhiteSpace(argumentsJson))
        {

            return null;

        }

        try
        {

            using JsonDocument doc = JsonDocument.Parse(argumentsJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {

                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {

                    ["value"] = doc.RootElement.Clone(),

                };

            }

            Dictionary<string, object?> map = new(StringComparer.Ordinal);

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {

                map[prop.Name] = prop.Value.Clone();

            }

            return map;

        }
        catch (JsonException)
        {

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {

                ["raw"] = argumentsJson,

            };

        }

    }

    private static ChatRole MapOpenAiStyleRoleToChatRole(string role)
    {

        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
        {

            return ChatRole.System;

        }

        if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
        {

            return ChatRole.Assistant;

        }

        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
        {

            return ChatRole.Tool;

        }

        if (string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase))
        {

            return ChatRole.System;

        }

        return ChatRole.User;

    }

}
