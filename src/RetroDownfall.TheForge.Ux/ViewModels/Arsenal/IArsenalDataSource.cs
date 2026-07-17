using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>
/// Testable seam for The Arsenal's MCP-server and built-in-tool operations. Implementations forward
/// to <see cref="RetroDownfall.TheForge.Ux.Services.Services.McpService"/> and
/// <see cref="RetroDownfall.TheForge.Ux.Services.Services.ToolInvokeService"/> and map
/// <see cref="ApiResponse{T}"/> failures to error strings without throwing.
/// </summary>
public interface IArsenalDataSource
{

    /// <summary>
    /// Lists MCP servers. On success <paramref name="Servers"/> is non-null (possibly empty) and
    /// <c>Error</c> is null; on failure <c>Servers</c> is null and <c>Error</c> describes the failure.
    /// </summary>
    Task<(IReadOnlyList<McpServerInfo>? Servers, string? Error)> ListMcpServersAsync(CancellationToken cancellationToken);

    Task<(bool Ok, string? Error)> StartServerAsync(string name, CancellationToken cancellationToken);

    Task<(bool Ok, string? Error)> StopServerAsync(string name, CancellationToken cancellationToken);

    Task<(bool Ok, string? Error)> RestartServerAsync(string name, CancellationToken cancellationToken);

    Task<(bool Success, string? Error)> ReloadMcpAsync(string? workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the workspace arsenal. On API failure returns <c>(null, error)</c>; a successful empty
    /// arsenal is <c>(dto-or-null, null)</c> — distinct from failure.
    /// </summary>
    Task<(WorkspaceArsenalDto? Arsenal, string? Error)> GetArsenalAsync(string? workingDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Invokes a built-in tool. On success <paramref name="Response"/> is non-null and <c>Error</c>
    /// is null; on failure <c>Response</c> is null and <c>Error</c> describes the failure.
    /// </summary>
    Task<(ToolInvokeResponse? Response, string? Error)> InvokeToolAsync(ToolInvokeRequest request, CancellationToken cancellationToken);

}
