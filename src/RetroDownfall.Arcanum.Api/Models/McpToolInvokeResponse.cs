using System.Text.Json;

namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Result body for <c>POST /api/mcp/tools/invoke</c> — Diagnostic MCP Invocation. <paramref name="Result"/>
/// is the tool's formatted output (text content blocks) parsed as JSON when possible, else a JSON
/// string. <paramref name="Truncated"/> is true when the output hit the code-owned MCP bridge cap.
/// </summary>
public sealed record McpToolInvokeResponse(
    JsonElement Result = default,
    string ServerName = "",
    string ToolName = "",
    long DurationMs = 0,
    bool Truncated = false);
