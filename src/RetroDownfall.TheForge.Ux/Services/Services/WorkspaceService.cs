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
        _apiClient.GetAsync("/api/workspaces", TheForgeJsonContext.Default.ApiResponseWorkspaceInfoArray, cancellationToken);

    /// <summary><c>GET /api/workspaces/{id}/files?relativePath=</c> — directory listing.</summary>
    public Task<ApiResponse<FileListResult>?> ListFilesAsync(
        string workspaceId,
        string? relativePath,
        bool? recursive,
        string? searchPattern,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files",
            ("relativePath", relativePath),
            ("recursive", recursive?.ToString()),
            ("searchPattern", searchPattern));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseFileListResult, cancellationToken);

    }

    /// <summary><c>GET /api/workspaces/{id}/files/info?relativePath=</c> — file/directory metadata.</summary>
    public Task<ApiResponse<FileEntry>?> GetFileInfoAsync(string workspaceId, string? relativePath, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/info",
            ("relativePath", relativePath));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseFileEntry, cancellationToken);

    }

    /// <summary><c>GET /api/workspaces/{id}/files/contents?relativePath=</c> — UTF-8 text contents.</summary>
    public Task<ApiResponse<FileReadResult>?> GetFileContentsAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/contents",
            ("relativePath", relativePath));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseFileReadResult, cancellationToken);

    }

    /// <summary><c>POST /api/workspaces/{id}/files/index</c> — triggers the server background
    /// re-index (202); gated by <c>Arcanum:Features:CodebaseRetrieval</c> plus valid embedding
    /// integration facts.</summary>
    public Task<ApiResponse<bool>?> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/index",
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

    /// <summary><c>PUT /api/workspaces/{id}/files/contents?relativePath=</c> — server-gated by <c>Arcanum:Workspaces:EnableFileWrite</c>; 403 <c>Workspace.FileWriteDisabled</c>.</summary>
    public Task<ApiResponse<FileWriteResult>?> WriteFileContentsAsync(
        string workspaceId,
        string relativePath,
        string content,
        CancellationToken cancellationToken) =>
        _apiClient.PutAsync(
            QueryStringBuilder.Build($"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/contents", ("relativePath", relativePath)),
            new FileWriteRequest(content),
            TheForgeJsonContext.Default.FileWriteRequest,
            TheForgeJsonContext.Default.ApiResponseFileWriteResult,
            cancellationToken);

    /// <summary><c>PATCH /api/workspaces/{id}/files/contents?relativePath=</c> — text-block replace; server-gated by <c>Arcanum:Workspaces:EnableFileWrite</c>.</summary>
    public Task<ApiResponse<TextBlockReplaceResult>?> ReplaceTextBlockAsync(
        string workspaceId,
        string relativePath,
        TextBlockReplaceRequest request,
        CancellationToken cancellationToken) =>
        _apiClient.PatchAsync(
            QueryStringBuilder.Build($"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/contents", ("relativePath", relativePath)),
            request,
            TheForgeJsonContext.Default.TextBlockReplaceRequest,
            TheForgeJsonContext.Default.ApiResponseTextBlockReplaceResult,
            cancellationToken);

    /// <summary><c>DELETE /api/workspaces/{id}/files?relativePath=</c> — 200 <c>ApiResponse&lt;FileDeleteResult&gt;</c>; server-gated by <c>Arcanum:Workspaces:EnableFileWrite</c>.</summary>
    public Task<ApiResponse<FileDeleteResult>?> DeleteFileAsync(
        string workspaceId,
        string relativePath,
        bool? recursive,
        CancellationToken cancellationToken) =>
        _apiClient.DeleteAsync(
            QueryStringBuilder.Build(
                $"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files",
                ("relativePath", relativePath),
                ("recursive", recursive?.ToString())),
            TheForgeJsonContext.Default.ApiResponseFileDeleteResult,
            cancellationToken);

    /// <summary><c>POST /api/workspaces/{id}/files/directory?relativePath=</c> — 201; server-gated by <c>Arcanum:Workspaces:EnableFileWrite</c>.</summary>
    public Task<ApiResponse<DirectoryCreateResult>?> CreateDirectoryAsync(string workspaceId, string relativePath, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            QueryStringBuilder.Build($"/api/workspaces/{Uri.EscapeDataString(workspaceId)}/files/directory", ("relativePath", relativePath)),
            TheForgeJsonContext.Default.ApiResponseDirectoryCreateResult,
            cancellationToken);

}
