using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>Body for <c>POST /v1/moderations</c>. See <c>docs/Arcanum.DESIGN.md</c> §11.18.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; endpoint tests cover wire parsing.
public sealed record OpenAiModerationRequest(
    [property: JsonPropertyName("input")] OpenAiModerationInput? Input,
    [property: JsonPropertyName("model")] string? Model = "omni-moderation-latest");
