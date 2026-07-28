using RetroDownfall.Arcanum.Core.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Mutable lifecycle state for one configured MCP server in the managed registry.
/// </summary>
internal sealed class ManagedMcpServerEntry
{

    private int _retired;

    public ManagedMcpServerEntry(
        string name,
        string? scopeWorkingDirectory,
        McpServerConfig config,
        McpServerTransport transport,
        bool alwaysOn,
        string? sourceDigest)
    {
        Name = name;

        ScopeWorkingDirectory = scopeWorkingDirectory;

        Config = config;

        Transport = transport;

        AlwaysOn = alwaysOn;

        SourceDigest = sourceDigest;

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

    /// <summary>
    /// SHA-256 of the exact workspace <c>mcp.json</c> bytes that produced this entry.
    /// Global entries have no workspace source digest.
    /// </summary>
    public string? SourceDigest { get; }

    public SemaphoreSlim Gate { get; }

    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public bool MarkRetired() =>
        Interlocked.Exchange(ref _retired, 1) == 0;

    /// <summary>
    /// Incremented each time a new subprocess transport is started; stale <c>Exited</c> handlers ignore mismatched generations.
    /// </summary>
    public long TransportGeneration { get; set; }

    public McpServerState State { get; set; } = McpServerState.Stopped;

    public IMcpClient? Client { get; set; }

    public Task? DetachedClientDisposal { get; set; }

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
internal readonly record struct LoadedMcpToolRow(McpBridgeTool Tool, McpServerConfig Config, IMcpClient Client);
