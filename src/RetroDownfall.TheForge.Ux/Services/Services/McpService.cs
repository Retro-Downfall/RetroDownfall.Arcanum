using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/mcp</c> and <c>GET /api/events/mcp</c> (SSE) for The Arsenal.</summary>
public sealed class McpService
{

    private readonly ArcanumApiClient _apiClient;

    private readonly ArcanumSseClient _sseClient;

    public McpService(ArcanumApiClient apiClient, ArcanumSseClient sseClient)
    {

        _apiClient = apiClient;

        _sseClient = sseClient;

    }

    public Task<ApiResponse<McpServerInfo[]>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/mcp", TheForgeJsonContext.Default.ApiResponseMcpServerInfoArray, cancellationToken);

    public IAsyncEnumerable<McpServerEvent> StreamEventsAsync(CancellationToken cancellationToken) =>
        _sseClient.StreamMcpEventsAsync(cancellationToken);

    public Task<ApiResponse<bool>?> StartAsync(string name, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/mcp/{Uri.EscapeDataString(name)}/start",
            TheForgeJsonContext.Default.ApiResponseBoolean, cancellationToken);

    public Task<ApiResponse<bool>?> StopAsync(string name, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/mcp/{Uri.EscapeDataString(name)}/stop",
            TheForgeJsonContext.Default.ApiResponseBoolean, cancellationToken);

    public Task<ApiResponse<bool>?> RestartAsync(string name, CancellationToken cancellationToken) =>
        _apiClient.PostAsync($"/api/mcp/{Uri.EscapeDataString(name)}/restart",
            TheForgeJsonContext.Default.ApiResponseBoolean, cancellationToken);

    public Task<ApiResponse<string>?> ReloadAsync(string? workingDirectory, CancellationToken cancellationToken) =>
        _apiClient.PostAsync("/api/mcp/reload",
            new OptionalWorkspaceRequest(workingDirectory),
            TheForgeJsonContext.Default.OptionalWorkspaceRequest,
            TheForgeJsonContext.Default.ApiResponseString, cancellationToken);

    public Task<ApiResponse<WorkspaceArsenalDto>?> GetArsenalAsync(string? workingDirectory, CancellationToken cancellationToken) =>
        _apiClient.PostAsync("/api/intelligence/arsenal",
            new OptionalWorkspaceRequest(workingDirectory),
            TheForgeJsonContext.Default.OptionalWorkspaceRequest,
            TheForgeJsonContext.Default.ApiResponseWorkspaceArsenalDto, cancellationToken);

}
