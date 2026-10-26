using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Body for <c>POST /v1/embeddings</c>. <see cref="EncodingFormat"/> defaults to <c>"float"</c>
/// when omitted (OpenAI convention); <see cref="Dimensions"/>, when supplied, must match the
/// configured width and the returned vectors. Per-request dimension changes are refused;
/// <see cref="User"/> is parsed but not enforced.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; endpoint tests cover wire parsing.
public sealed record OpenAiEmbeddingRequest(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("input")] OpenAiEmbeddingInput? Input,
    [property: JsonPropertyName("encoding_format")] string? EncodingFormat = null,
    [property: JsonPropertyName("dimensions")] int? Dimensions = null,
    [property: JsonPropertyName("user")] string? User = null);
