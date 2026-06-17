using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Manages MCP server connections, per-server lifecycle, and merged tool surfaces.
/// </summary>
public interface IMcpConnectionManager
{

    /// <summary>
    /// Loads the global server registry and starts <see cref="McpServerInfo.AlwaysOn"/> global entries.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully stops all managed MCP servers (host shutdown).
    /// </summary>
    Task StopAllAsync(CancellationToken cancellationToken = default);

    Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default);

    Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default);

    Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default);

    Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default);

    Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default);

    Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default);

    Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records operator approval for the workspace-local <c>mcp.json</c> at <paramref name="workingDirectory"/>.
    /// </summary>
    Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default);

}
