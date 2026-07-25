using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Projects Arcanum's additive usage contract onto OpenAI's nested completion-token detail shape.
/// Reasoning tokens remain included in completion and total counts.
/// </summary>
public sealed class OpenAiChatUsageJsonConverter : JsonConverter<ChatCompletionUsage>
{
    public override ChatCompletionUsage Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ChatCompletionUsage(0, 0, 0);
        }

        int promptTokens = ReadInt32(root, "prompt_tokens");
        int completionTokens = ReadInt32(root, "completion_tokens");
        int totalTokens = ReadInt32(root, "total_tokens");
        int cachedTokens = ReadInt32(root, "cached_tokens");
        int reasoningTokens = 0;

        if (root.TryGetProperty("prompt_tokens_details", out JsonElement promptDetails)
            && TryReadInt32(promptDetails, "cached_tokens", out int nestedCachedTokens))
        {
            cachedTokens = nestedCachedTokens;
        }

        if (root.TryGetProperty("completion_tokens_details", out JsonElement completionDetails)
            && TryReadInt32(completionDetails, "reasoning_tokens", out int nestedReasoningTokens))
        {
            reasoningTokens = nestedReasoningTokens;
        }

        return new ChatCompletionUsage(
            promptTokens,
            completionTokens,
            totalTokens,
            cachedTokens,
            reasoningTokens);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ChatCompletionUsage value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("prompt_tokens", value.PromptTokens);
        writer.WriteNumber("completion_tokens", value.CompletionTokens);
        writer.WriteNumber("total_tokens", value.TotalTokens);

        if (value.CachedTokens != 0)
        {
            writer.WritePropertyName("prompt_tokens_details");
            writer.WriteStartObject();
            writer.WriteNumber("cached_tokens", value.CachedTokens);
            writer.WriteEndObject();
        }

        if (value.ReasoningTokens != 0)
        {
            writer.WritePropertyName("completion_tokens_details");
            writer.WriteStartObject();
            writer.WriteNumber("reasoning_tokens", value.ReasoningTokens);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static int ReadInt32(JsonElement parent, string propertyName) =>
        TryReadInt32(parent, propertyName, out int parsed) ? parsed : 0;

    private static bool TryReadInt32(
        JsonElement parent,
        string propertyName,
        out int parsed)
    {
        parsed = 0;

        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out parsed);
    }
}
