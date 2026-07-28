using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.McpToolInvokeResponse</c> (result of
/// <c>POST /api/mcp/tools/invoke</c> — Diagnostic MCP Invocation). <c>Result</c> is the tool's
/// formatted output parsed as JSON when possible, else a JSON string. <c>Truncated</c> is true when
/// the output hit the code-owned MCP bridge cap. camelCase wire via
/// <c>TheForgeJsonContext</c>.
/// </summary>
public sealed record McpToolInvokeResponse
{

    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    [JsonPropertyName("serverName")]
    public string ServerName { get; init; } = string.Empty;

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

}
