using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models.OpenAi;

/// <summary>
/// OpenAI-shaped error detail for <c>/v1/*</c> failures. Mirrored from Arcanum Api —
/// The Forge must not reference <c>RetroDownfall.Arcanum.Api</c>.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiErrorDetail(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("param")] string? Param = null,
    [property: JsonPropertyName("code")] string? Code = null);

/// <summary>OpenAI-shaped error envelope (<c>{ "error": { ... } }</c>) for <c>/v1/*</c>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; client tests cover wire deserialization.
public sealed record OpenAiErrorResponse(
    [property: JsonPropertyName("error")] OpenAiErrorDetail Error);
