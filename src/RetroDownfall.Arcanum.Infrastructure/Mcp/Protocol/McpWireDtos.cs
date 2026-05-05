using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

/// <summary>
/// MCP <c>initialize</c> request <c>params</c> (subset used by this client).
/// </summary>
public sealed record McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required McpClientCapabilities Capabilities { get; init; }

    [JsonPropertyName("clientInfo")]
    public required McpClientInfo ClientInfo { get; init; }
}

/// <summary>
/// MCP client capabilities object (may be empty on the wire).
/// </summary>
public sealed record McpClientCapabilities
{
}

/// <summary>
/// MCP <c>clientInfo</c> on initialize.
/// </summary>
public sealed record McpClientInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// MCP <c>tools/list</c> request <c>params</c>.
/// </summary>
public sealed record McpToolsListParams
{
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }
}

/// <summary>
/// MCP <c>tools/call</c> request <c>params</c>.
/// </summary>
public sealed record McpToolsCallParams
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public required JsonElement Arguments { get; init; }
}

/// <summary>
/// Empty JSON object used when a tool omits <c>inputSchema</c>.
/// </summary>
public sealed record McpEmptyJsonObject
{
}

/// <summary>
/// MCP server <c>serverInfo</c> on <c>initialize</c> result (in-process server).
/// </summary>
public sealed record McpServerInfoWire
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// MCP server <c>capabilities</c> on <c>initialize</c> result (minimal stub).
/// </summary>
public sealed record McpServerCapabilitiesWire
{
    [JsonPropertyName("tools")]
    public McpEmptyJsonObject Tools { get; init; } = new();
}

/// <summary>
/// MCP <c>initialize</c> <c>result</c> body from the in-process Arcanum server.
/// </summary>
public sealed record McpInitializeServerResult
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required McpServerCapabilitiesWire Capabilities { get; init; }

    [JsonPropertyName("serverInfo")]
    public required McpServerInfoWire ServerInfo { get; init; }
}

/// <summary>
/// One MCP tool descriptor returned in <c>tools/list</c>. <see cref="InputSchema"/> is a verbatim JSON Schema object built ahead-of-time by the in-process server.
/// </summary>
public sealed record McpToolDefinitionWire
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("inputSchema")]
    public required JsonElement InputSchema { get; init; }
}

/// <summary>
/// MCP <c>tools/list</c> <c>result</c> body. <see cref="Tools"/> carries real tool descriptors for the in-process server.
/// </summary>
public sealed record McpToolsListResultWire
{
    [JsonPropertyName("tools")]
    public McpToolDefinitionWire[] Tools { get; init; } = [];
}

/// <summary>
/// One MCP text content block in a <c>tools/call</c> result.
/// </summary>
public sealed record McpToolContentTextWire
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// MCP <c>tools/call</c> <c>result</c> body (stub).
/// </summary>
public sealed record McpToolsCallResultWire
{
    [JsonPropertyName("content")]
    public required McpToolContentTextWire[] Content { get; init; }

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>read_file_chunk</c> tool. Line numbers are 1-based and inclusive.
/// </summary>
public sealed record ReadFileChunkParams
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("startLine")]
    public required int StartLine { get; init; }

    [JsonPropertyName("endLine")]
    public required int EndLine { get; init; }
}

/// <summary>
/// Arguments accepted by the in-process <c>replace_text_block</c> tool. <see cref="ExactSearchText"/> must be a verbatim block from the file (whitespace and newlines preserved).
/// </summary>
public sealed record ReplaceTextBlockParams
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("exactSearchText")]
    public required string ExactSearchText { get; init; }

    [JsonPropertyName("replacementText")]
    public required string ReplacementText { get; init; }
}

