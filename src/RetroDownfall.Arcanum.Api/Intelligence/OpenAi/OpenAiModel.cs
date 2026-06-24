using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; mapper tests cover wire serialization.
public sealed record OpenAiModel(
    string Id,
    [property: JsonPropertyName("object")] string ObjectKind = "model",
    long Created = 0,
    [property: JsonPropertyName("owned_by")] string OwnedBy = "system");
