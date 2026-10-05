using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Request body for <c>POST /api/mcp/tools/invoke</c> — Diagnostic MCP Invocation. Policy-constrained:
/// external MCP tools only; the internal <c>arcanum-internal</c> server and reserved Master-pipeline
/// names are blocked. Requires a running, trusted MCP server. Not model execution; not unauthenticated.
/// </summary>
/// <param name="ToolName">The tool to invoke.</param>
/// <param name="Arguments">The tool's arguments, as the untyped JSON the tool's own schema describes.</param>
/// <param name="ServerName">Optional disambiguator when the tool name is provided by more than one external server.</param>
/// <param name="WorkingDirectory">Optional workspace path to scope the visible MCP surface (must be trusted for workspace-local servers).</param>
public sealed record McpToolInvokeRequest(
    string ToolName = "",
    JsonElement Arguments = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServerName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkingDirectory = null);
