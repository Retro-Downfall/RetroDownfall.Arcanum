namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Observable status of one configured MCP server for lifecycle APIs.
/// </summary>
public sealed record McpServerInfo(
    string Name,
    string? WorkingDirectory,
    McpServerTransport Transport,
    bool AlwaysOn,
    string? Command,
    string[] Arguments,
    string? Url,
    McpServerState State,
    string? ErrorMessage,
    string[] Tools,
    DateTimeOffset? LastConnectedAt);
