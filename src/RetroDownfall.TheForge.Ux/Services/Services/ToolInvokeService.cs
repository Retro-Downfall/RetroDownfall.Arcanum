using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.TheForge.Core.Models;

using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>POST /api/tools/invoke</c> — invoke a built-in tool by name (The Scrying Pool).</summary>
public sealed class ToolInvokeService
{

    private readonly ArcanumApiClient _apiClient;

    public ToolInvokeService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<ToolInvokeResponse>?> InvokeAsync(ToolInvokeRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync("/api/tools/invoke", request,
            TheForgeJsonContext.Default.ToolInvokeRequest,
            TheForgeJsonContext.Default.ApiResponseToolInvokeResponse, cancellationToken);

}
