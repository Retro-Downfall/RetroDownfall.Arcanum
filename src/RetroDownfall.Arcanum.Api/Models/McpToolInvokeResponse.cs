using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Result body for <c>POST /api/mcp/tools/invoke</c> — Diagnostic MCP Invocation. <see cref="Result"/>
/// is the tool's formatted output (text content blocks) parsed as JSON when possible, else a JSON
/// string. <see cref="Truncated"/> is true when the output hit the configured <c>ToolOutputCapBytes</c>.
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
