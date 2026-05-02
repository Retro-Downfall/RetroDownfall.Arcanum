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
