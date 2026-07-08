using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
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
        _apiClient.GetAsync("/api/mcp", ForgeJsonContext.Default.ApiResponseMcpServerInfoArray, cancellationToken);

    public IAsyncEnumerable<McpServerEvent> StreamEventsAsync(CancellationToken cancellationToken) =>
        _sseClient.StreamMcpEventsAsync(cancellationToken);

}
