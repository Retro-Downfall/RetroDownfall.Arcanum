using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/workspaces</c> for The Atelier's "Workspaces" root.</summary>
public sealed class WorkspaceService
{

    private readonly ArcanumApiClient _apiClient;

    public WorkspaceService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<WorkspaceInfo[]>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/workspaces", ForgeJsonContext.Default.ApiResponseWorkspaceInfoArray, cancellationToken);

}
