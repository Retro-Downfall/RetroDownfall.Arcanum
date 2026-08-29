using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Request body for <c>POST /api/mcp/tools/invoke</c> — Diagnostic MCP Invocation. Policy-constrained:
/// external MCP tools only; the internal <c>arcanum-internal</c> server and reserved Master-pipeline
/// names are blocked. Requires a running, trusted MCP server. Not model execution; not unauthenticated.
/// </summary>
public sealed record McpToolInvokeRequest
{

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }

    /// <summary>Optional disambiguator when the tool name is provided by more than one external server.</summary>
    [JsonPropertyName("serverName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerName { get; init; }

    /// <summary>Optional workspace path to scope the visible MCP surface (must be trusted for workspace-local servers).</summary>
    [JsonPropertyName("workingDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; init; }

}
