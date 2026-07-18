using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.WeaveInspector;

/// <summary>
/// Data-source seam for The Weave Inspector (Phase 7). Wraps <see cref="WeaveInspectorService"/>: read-only
/// index status + chunk browsing, plus the destructive embeddings reset (caller-scoped, default
/// <c>workspace_file</c>). Tests fake this interface. Disabled/feature-off states surface as
/// <c>Embeddings.FeatureDisabled</c> in <see cref="DataSourceResult{T}.ErrorCode"/> where applicable.
/// </summary>
public interface IWeaveInspectorDataSource
{

    Task<DataSourceResult<WorkspaceIndexStatusDto>> GetIndexStatusAsync(string workspaceId, CancellationToken cancellationToken);

    Task<DataSourceResult<WorkspaceFileChunkPage>> GetChunksAsync(string workspaceId, string? relativePath, int limit, int offset, CancellationToken cancellationToken);

    Task<DataSourceResult<EmbeddingsResetResult>> ResetEmbeddingsAsync(string scope, CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="IWeaveInspectorDataSource"/>.</summary>
public sealed class WeaveInspectorDataSource : IWeaveInspectorDataSource
{

    private readonly WeaveInspectorService _service;

    public WeaveInspectorDataSource(WeaveInspectorService service)
    {

        _service = service;

    }

    public async Task<DataSourceResult<WorkspaceIndexStatusDto>> GetIndexStatusAsync(string workspaceId, CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceIndexStatusDto>? response = await _service
            .GetIndexStatusAsync(workspaceId, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<WorkspaceIndexStatusDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<WorkspaceFileChunkPage>> GetChunksAsync(string workspaceId, string? relativePath, int limit, int offset, CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceFileChunkPage>? response = await _service
            .GetChunksAsync(workspaceId, relativePath, limit, offset, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<WorkspaceFileChunkPage>.FromResponse(response);

    }

    public async Task<DataSourceResult<EmbeddingsResetResult>> ResetEmbeddingsAsync(string scope, CancellationToken cancellationToken)
    {

        ApiResponse<EmbeddingsResetResult>? response = await _service
            .ResetEmbeddingsAsync(scope, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<EmbeddingsResetResult>.FromResponse(response);

    }

}
