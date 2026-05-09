using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiChatChunk(
    string Id,
    [property: JsonPropertyName("object")] string ObjectKind,
    long Created,
    string Model,
    List<OpenAiChatStreamChoice> Choices);
