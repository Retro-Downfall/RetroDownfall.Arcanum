using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Lifecycle state of a managed MCP server instance.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<McpServerState>))]
public enum McpServerState
{

    [JsonStringEnumMemberName("stopped")]
    Stopped,

    [JsonStringEnumMemberName("starting")]
    Starting,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("restarting")]
    Restarting,

}
