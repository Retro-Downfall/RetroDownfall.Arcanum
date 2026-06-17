using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Terminal SSE payload when inference fails mid-stream: standard <c>chat.completion.chunk</c>
/// framing plus an OpenAI-shaped <c>error</c> object (not <c>delta.content</c>).
/// </summary>
public sealed record OpenAiChatStreamErrorChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string ObjectKind,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] List<OpenAiChatStreamChoice> Choices,
    [property: JsonPropertyName("error")] OpenAiErrorDetail Error,
    [property: JsonPropertyName("system_fingerprint")] string? SystemFingerprint = null);
