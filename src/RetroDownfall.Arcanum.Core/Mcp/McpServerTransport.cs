using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Wire transport for an MCP server configuration entry.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<McpServerTransport>))]
public enum McpServerTransport
{

    [JsonStringEnumMemberName("stdio")]
    Stdio,

    [JsonStringEnumMemberName("sse")]
    Sse,

}
