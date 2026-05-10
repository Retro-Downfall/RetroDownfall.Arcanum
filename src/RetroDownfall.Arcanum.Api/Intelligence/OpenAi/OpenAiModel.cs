using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

public sealed record OpenAiModel(
    string Id,
    [property: JsonPropertyName("object")] string ObjectKind = "model",
    long Created = 0,
    [property: JsonPropertyName("owned_by")] string OwnedBy = "system");
