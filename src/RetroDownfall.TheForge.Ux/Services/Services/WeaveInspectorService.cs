using System.Globalization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the Phase 7 read-only Weave Inspector routes + the existing embeddings reset route. Status/chunks
/// are read-only inspection of a workspace's indexed chunks; reset is destructive and always requires
/// <c>?confirm=true</c> (the caller scopes it, defaulting to <c>workspace_file</c> from the inspector).
/// </summary>
public sealed class WeaveInspectorService
{

    private readonly ArcanumApiClient _apiClient;

    public WeaveInspectorService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    /// <summary><c>GET /api/workspaces/{id}/files/index/status</c> — read-only indexing status.</summary>
    public Task<ApiResponse<WorkspaceIndexStatusDto>?> GetIndexStatusAsync(string workspaceId, CancellationToken cancellationToken) =>
        _apiClient.GetAsync(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/index/status",
            TheForgeJsonContext.Default.ApiResponseWorkspaceIndexStatusDto,
            cancellationToken);

    /// <summary><c>GET /api/workspaces/{id}/files/chunks?relativePath=&amp;limit=&amp;offset=</c> — bounded chunk previews.</summary>
    public Task<ApiResponse<WorkspaceFileChunkPage>?> GetChunksAsync(
        string workspaceId,
        string? relativePath,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/chunks",
            ("relativePath", relativePath),
            ("limit", limit.ToString(CultureInfo.InvariantCulture)),
            ("offset", offset.ToString(CultureInfo.InvariantCulture)));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseWorkspaceFileChunkPage, cancellationToken);

    }

    /// <summary><c>POST /api/embeddings/reset?scope=&amp;confirm=true</c> — destructive; caller chooses scope.</summary>
    public Task<ApiResponse<EmbeddingsResetResult>?> ResetEmbeddingsAsync(string scope, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            QueryStringBuilder.Build("/api/embeddings/reset", ("scope", scope), ("confirm", "true")),
            TheForgeJsonContext.Default.ApiResponseEmbeddingsResetResult,
            cancellationToken);

}
