using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Request body for <c>POST /api/tools/invoke</c> — directly executes a built-in tool by name.
/// </summary>
public sealed record ToolInvokeRequest
{

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }

}
