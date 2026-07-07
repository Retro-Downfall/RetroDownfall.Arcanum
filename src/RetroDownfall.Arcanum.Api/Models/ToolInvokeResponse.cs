using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Result body for <c>POST /api/tools/invoke</c> — the raw tool output serialized as JSON.
/// </summary>
public sealed record ToolInvokeResponse
{

    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

}
