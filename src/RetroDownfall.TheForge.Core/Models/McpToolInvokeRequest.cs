using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Re-declared mirror of <c>RetroDownfall.Arcanum.Api.Models.McpToolInvokeRequest</c> (body of
/// <c>POST /api/mcp/tools/invoke</c> — Diagnostic MCP Invocation: policy-constrained external MCP
/// tool invoke by name). Kept in TheForge.Core to avoid referencing the ASP.NET-heavy Api project.
/// camelCase wire via <c>TheForgeJsonContext</c>.
/// </summary>
public sealed record McpToolInvokeRequest
{

    [JsonPropertyName("toolName")]
    public string ToolName { get; init; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonElement Arguments { get; init; }

    [JsonPropertyName("serverName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerName { get; init; }

    [JsonPropertyName("workingDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; init; }

}
