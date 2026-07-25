using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
// Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiChatResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string ObjectKind,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<OpenAiChatChoice> Choices,
    [property: JsonPropertyName("usage")]
    [property: JsonConverter(typeof(OpenAiChatUsageJsonConverter))]
    ChatCompletionUsage? Usage,
    [property: JsonPropertyName("system_fingerprint")] string? SystemFingerprint = null,
    [property: JsonPropertyName("service_tier")] string? ServiceTier = null);
