namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// MCP host client and transport security limits.
/// </summary>
public sealed record McpSettings
{

    public int RequestTimeoutSeconds { get; init; } = 60;

    public int MaxPaginationPages { get; init; } = 32;

    public bool BootstrapBlocksStartup { get; init; } = true;

    public int MaxServers { get; init; } = 50;

    public int MaxToolsPerServer { get; init; } = 256;

    public int MaxToolsPerListPage { get; init; } = 64;

    public int MaxToolsTotalBytes { get; init; } = 1_048_576;

    public int MaxJsonRpcLineBytes { get; init; } = 2_228_224;

}
