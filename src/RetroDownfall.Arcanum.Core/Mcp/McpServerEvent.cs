using RetroDownfall.Arcanum.Core.Events;

namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Lifecycle frame for a managed MCP server, streamed via <c>GET /api/events/mcp</c>.
/// </summary>
public sealed record McpServerEvent : ArcanumEvent
{

    public McpServerEvent(DateTimeOffset timestamp) : base(timestamp)
    {
    }

    public string ServerName { get; init; } = string.Empty;

    public McpServerState State { get; init; }

    public string? Message { get; init; }

    public string[] Tools { get; init; } = Array.Empty<string>();

}
