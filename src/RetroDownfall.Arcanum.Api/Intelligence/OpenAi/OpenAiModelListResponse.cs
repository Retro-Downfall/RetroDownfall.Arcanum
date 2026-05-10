using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiModelListResponse(
    List<OpenAiModel> Data,
    [property: JsonPropertyName("object")] string ObjectKind = "list");
