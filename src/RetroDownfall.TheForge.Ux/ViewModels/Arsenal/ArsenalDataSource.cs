using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>API-backed <see cref="IArsenalDataSource"/> — wraps <see cref="McpService"/> + <see cref="ToolInvokeService"/>.</summary>
public sealed class ArsenalDataSource : IArsenalDataSource
{

    private readonly McpService _mcpService;

    private readonly ToolInvokeService _toolInvokeService;

    public ArsenalDataSource(McpService mcpService, ToolInvokeService toolInvokeService)
    {

        _mcpService = mcpService;

        _toolInvokeService = toolInvokeService;

    }

    public async Task<(IReadOnlyList<McpServerInfo>? Servers, string? Error)> ListMcpServersAsync(CancellationToken cancellationToken)
    {

        ApiResponse<McpServerInfo[]>? response = await _mcpService.ListAsync(cancellationToken).ConfigureAwait(false);

        if (response is { IsSuccess: true })
        {

            IReadOnlyList<McpServerInfo> servers = response.Data ?? [];

            return (servers, null);

        }

        return (null, response?.Error?.Message ?? "Failed to list MCP servers.");

    }

    public async Task<bool> StartServerAsync(string name, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _mcpService.StartAsync(name, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: true };

    }

    public async Task<bool> StopServerAsync(string name, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _mcpService.StopAsync(name, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: true };

    }

    public async Task<bool> RestartServerAsync(string name, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _mcpService.RestartAsync(name, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: true };

    }

    public async Task<(bool Success, string? Error)> ReloadMcpAsync(string? workingDirectory, CancellationToken cancellationToken)
    {

        ApiResponse<string>? response = await _mcpService.ReloadAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

        if (response is { IsSuccess: true })
        {

            return (true, null);

        }

        return (false, response?.Error?.Message ?? "Failed to reload MCP configuration.");

    }

    public async Task<(WorkspaceArsenalDto? Arsenal, string? Error)> GetArsenalAsync(string? workingDirectory, CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceArsenalDto>? response = await _mcpService.GetArsenalAsync(workingDirectory, cancellationToken).ConfigureAwait(false);

        if (response is { IsSuccess: true })
        {

            return (response.Data, null);

        }

        return (null, response?.Error?.Message ?? "Failed to load arsenal.");

    }

    public async Task<ToolInvokeResponse?> InvokeToolAsync(ToolInvokeRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<ToolInvokeResponse>? response = await _toolInvokeService.InvokeAsync(request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } result } ? result : null;

    }

}
