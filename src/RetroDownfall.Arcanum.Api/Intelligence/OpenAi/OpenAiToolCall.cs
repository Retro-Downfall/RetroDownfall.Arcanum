using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

/// <summary>
/// Tool call as it appears on a non-streaming assistant message (<c>choices[].message.tool_calls[]</c>).
/// </summary>
public sealed record OpenAiToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] OpenAiFunctionCall Function);

/// <summary>
/// Tool call as it appears inside a streaming <c>delta.tool_calls[]</c> entry. All fields except
/// <c>index</c> are optional because OpenAI emits partial deltas (id and name on first chunk,
/// argument fragments on subsequent chunks). Arcanum currently emits one complete delta per call.
/// </summary>
public sealed record OpenAiStreamToolCall(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("function")] OpenAiFunctionCall? Function = null);

public sealed record OpenAiFunctionCall(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? Arguments);
