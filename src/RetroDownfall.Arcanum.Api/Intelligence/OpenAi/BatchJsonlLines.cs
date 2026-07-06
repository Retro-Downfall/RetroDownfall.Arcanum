using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// One line of a <c>/v1/batches</c> input JSONL file — OpenAI's real batch request wrapper shape
/// (<c>custom_id</c>/<c>method</c>/<c>url</c>/<c>body</c>), not a bare <c>OpenAiChatRequest</c>. See
/// DESIGN.md §11.21.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; processor tests cover wire parsing.
public sealed record BatchJsonlRequestLine(
    [property: JsonPropertyName("custom_id")] string CustomId,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("body")] OpenAiChatRequest Body);

/// <summary>One line of a <c>/v1/batches</c> output JSONL file — a successful or failed per-request result.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; processor tests cover wire parsing.
public sealed record BatchJsonlResponseLine(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("custom_id")] string CustomId,
    [property: JsonPropertyName("response")] BatchJsonlResponseBody? Response,
    [property: JsonPropertyName("error")] BatchJsonlError? Error);

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; processor tests cover wire parsing.
public sealed record BatchJsonlResponseBody(
    [property: JsonPropertyName("status_code")] int StatusCode,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("body")] OpenAiChatResponse Body);

[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible JSON contract POCO; processor tests cover wire parsing.
public sealed record BatchJsonlError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>One line of a <c>/v1/batches</c> *error* JSONL file — a line that could not even be parsed as a <see cref="BatchJsonlRequestLine"/>.</summary>
[ExcludeFromCodeCoverage] // Reason: OpenAI-compatible-adjacent JSON contract POCO; processor tests cover wire parsing.
public sealed record BatchJsonlParseError(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("error")] string Error);
