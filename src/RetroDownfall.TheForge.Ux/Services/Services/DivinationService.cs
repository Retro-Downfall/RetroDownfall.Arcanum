using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps Arcanum's semantic search ("Divination") routes across sessions and workspace files.</summary>
public sealed class DivinationService
{

    private readonly ArcanumApiClient _apiClient;

    public DivinationService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<SemanticSearchResult>?> SearchSessionsAsync(SemanticSearchRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/sessions/divine",
            request,
            TheForgeJsonContext.Default.SemanticSearchRequest,
            TheForgeJsonContext.Default.ApiResponseSemanticSearchResult,
            cancellationToken);

    public Task<ApiResponse<WorkspaceSearchResult[]>?> SearchWorkspaceFilesAsync(
        string workspaceId,
        WorkspaceSemanticSearchRequest request,
        CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/divine",
            request,
            TheForgeJsonContext.Default.WorkspaceSemanticSearchRequest,
            TheForgeJsonContext.Default.ApiResponseWorkspaceSearchResultArray,
            cancellationToken);

    /// <summary><c>POST /api/saga/divine</c> — Saga Divination; gated by Embeddings+SagaEnabled (503 <c>Embeddings.FeatureDisabled</c>).</summary>
    public Task<ApiResponse<SagaSearchResult>?> SearchSagaAsync(SagaSearchRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/saga/divine",
            request,
            TheForgeJsonContext.Default.SagaSearchRequest,
            TheForgeJsonContext.Default.ApiResponseSagaSearchResult,
            cancellationToken);

}
