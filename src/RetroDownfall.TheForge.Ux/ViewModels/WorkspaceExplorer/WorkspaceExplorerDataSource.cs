using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

/// <summary>
/// Data-source seam for the Workspace Explorer. Read paths (browse/info/contents/index/divine) are
/// the primary surface; write/modify/delete are server-gated by <c>Arcanum:Workspaces:EnableFileWrite</c>
/// and surface <c>Workspace.FileWriteDisabled</c> via <see cref="DataSourceResult{T}.ErrorCode"/>.
/// Tests fake this interface.
/// </summary>
public interface IWorkspaceExplorerDataSource
{

    Task<DataSourceResult<WorkspaceInfo[]>> ListWorkspacesAsync(CancellationToken cancellationToken);

    Task<DataSourceResult<FileListResult>> ListFilesAsync(string workspaceId, string? relativePath, bool? recursive, string? searchPattern, CancellationToken cancellationToken);

    Task<DataSourceResult<FileEntry>> GetFileInfoAsync(string workspaceId, string? relativePath, CancellationToken cancellationToken);

    Task<DataSourceResult<FileReadResult>> GetFileContentsAsync(string workspaceId, string relativePath, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken);

    Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken);

    Task<DataSourceResult<FileWriteResult>> WriteFileContentsAsync(string workspaceId, string relativePath, string content, CancellationToken cancellationToken);

    Task<DataSourceResult<TextBlockReplaceResult>> ReplaceTextBlockAsync(string workspaceId, string relativePath, string oldString, string newString, int? expectedReplacements, CancellationToken cancellationToken);

    Task<DataSourceResult<FileDeleteResult>> DeleteFileAsync(string workspaceId, string relativePath, bool? recursive, CancellationToken cancellationToken);

    Task<DataSourceResult<DirectoryCreateResult>> CreateDirectoryAsync(string workspaceId, string relativePath, CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="IWorkspaceExplorerDataSource"/> — wraps <see cref="WorkspaceService"/> (browse/index/write) and <see cref="DivinationService"/> (workspace file Divination).</summary>
public sealed class WorkspaceExplorerDataSource : IWorkspaceExplorerDataSource
{

    private readonly WorkspaceService _workspaceService;

    private readonly DivinationService _divinationService;

    public WorkspaceExplorerDataSource(WorkspaceService workspaceService, DivinationService divinationService)
    {

        _workspaceService = workspaceService;

        _divinationService = divinationService;

    }

    public async Task<DataSourceResult<WorkspaceInfo[]>> ListWorkspacesAsync(CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceInfo[]>? response = await _workspaceService.ListAsync(cancellationToken).ConfigureAwait(false);

        return DataSourceResult<WorkspaceInfo[]>.FromResponse(response);

    }

    public async Task<DataSourceResult<FileListResult>> ListFilesAsync(string workspaceId, string? relativePath, bool? recursive, string? searchPattern, CancellationToken cancellationToken)
    {

        ApiResponse<FileListResult>? response = await _workspaceService
            .ListFilesAsync(workspaceId, relativePath, recursive, searchPattern, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<FileListResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<FileEntry>> GetFileInfoAsync(string workspaceId, string? relativePath, CancellationToken cancellationToken)
    {

        ApiResponse<FileEntry>? response = await _workspaceService
            .GetFileInfoAsync(workspaceId, relativePath, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<FileEntry>.FromResponse(response);

    }

    public async Task<DataSourceResult<FileReadResult>> GetFileContentsAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
    {

        ApiResponse<FileReadResult>? response = await _workspaceService
            .GetFileContentsAsync(workspaceId, relativePath, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<FileReadResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<bool>> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _workspaceService.IndexWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<bool>.FromResponse(response);

    }

    public async Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceSearchResult[]>? response = await _divinationService
            .SearchWorkspaceFilesAsync(workspaceId, request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<WorkspaceSearchResult[]>.FromResponse(response);

    }

    public async Task<DataSourceResult<FileWriteResult>> WriteFileContentsAsync(string workspaceId, string relativePath, string content, CancellationToken cancellationToken)
    {

        ApiResponse<FileWriteResult>? response = await _workspaceService
            .WriteFileContentsAsync(workspaceId, relativePath, content, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<FileWriteResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<TextBlockReplaceResult>> ReplaceTextBlockAsync(string workspaceId, string relativePath, string oldString, string newString, int? expectedReplacements, CancellationToken cancellationToken)
    {

        TextBlockReplaceRequest request = new(oldString, newString, expectedReplacements);

        ApiResponse<TextBlockReplaceResult>? response = await _workspaceService
            .ReplaceTextBlockAsync(workspaceId, relativePath, request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<TextBlockReplaceResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<FileDeleteResult>> DeleteFileAsync(string workspaceId, string relativePath, bool? recursive, CancellationToken cancellationToken)
    {

        ApiResponse<FileDeleteResult>? response = await _workspaceService
            .DeleteFileAsync(workspaceId, relativePath, recursive, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<FileDeleteResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<DirectoryCreateResult>> CreateDirectoryAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
    {

        ApiResponse<DirectoryCreateResult>? response = await _workspaceService
            .CreateDirectoryAsync(workspaceId, relativePath, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<DirectoryCreateResult>.FromResponse(response);

    }

}
