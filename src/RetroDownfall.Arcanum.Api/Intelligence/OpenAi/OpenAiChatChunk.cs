using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiChatChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string ObjectKind,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<OpenAiChatStreamChoice> Choices,
    [property: JsonPropertyName("usage")] ChatCompletionUsage? Usage = null,
    [property: JsonPropertyName("system_fingerprint")] string? SystemFingerprint = null);
