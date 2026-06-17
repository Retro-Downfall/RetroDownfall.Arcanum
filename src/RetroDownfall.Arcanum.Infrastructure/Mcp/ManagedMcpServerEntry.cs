using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Mutable lifecycle state for one configured MCP server in the managed registry.
/// </summary>
internal sealed class ManagedMcpServerEntry
{

    public ManagedMcpServerEntry(
        string name,
        string? scopeWorkingDirectory,
        McpServerConfig config,
        McpServerTransport transport,
        bool alwaysOn)
    {
        Name = name;

        ScopeWorkingDirectory = scopeWorkingDirectory;

        Config = config;

        Transport = transport;

        AlwaysOn = alwaysOn;

        Gate = new SemaphoreSlim(1, 1);
    }

    public string Name { get; }

    /// <summary>
    /// <c>null</c> for global <c>mcp.json</c> entries; workspace root path for workspace-local entries.
    /// </summary>
    public string? ScopeWorkingDirectory { get; }

    public McpServerConfig Config { get; }

    public McpServerTransport Transport { get; }

    public bool AlwaysOn { get; }

    public SemaphoreSlim Gate { get; }

    /// <summary>
    /// Incremented each time a new subprocess transport is started; stale <c>Exited</c> handlers ignore mismatched generations.
    /// </summary>
    public long TransportGeneration { get; set; }

    public McpServerState State { get; set; } = McpServerState.Stopped;

    public McpClient? Client { get; set; }

    public string[] Tools { get; set; } = [];

    public string? ErrorMessage { get; set; }

    public DateTimeOffset? LastConnectedAt { get; set; }

    /// <summary>
    /// When set, automatic AlwaysOn restart attempts are deferred until this UTC instant.
    /// </summary>
    public DateTimeOffset? RestartAfterUtc { get; set; }

    public List<LoadedMcpToolRow> LoadedTools { get; } = [];

    /// <summary>
    /// Returns true when a transport-ended handler captured the current generation (not superseded by restart).
    /// </summary>
    public static bool IsTransportGenerationCurrent(long capturedGeneration, long currentGeneration)
        => capturedGeneration == currentGeneration;

}

/// <summary>
/// Tool row bound to a managed server client.
/// </summary>
internal readonly record struct LoadedMcpToolRow(McpBridgeTool Tool, McpServerConfig Config, McpClient Client);
