using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Divination;

/// <summary>Data-source seam for the Divination surface; tests fake this interface.</summary>
public interface IDivinationDataSource
{

    Task<DataSourceResult<SemanticSearchResult>> DivineSessionsAsync(SemanticSearchRequest request, CancellationToken cancellationToken);

    Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken);

    Task<DataSourceResult<SagaSearchResult>> DivineSagaAsync(SagaSearchRequest request, CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="IDivinationDataSource"/> — wraps <see cref="DivinationService"/>. Each route is server-gated on Embeddings (+ the relevant sub-flag); a disabled feature surfaces as <c>Embeddings.FeatureDisabled</c> in <see cref="DataSourceResult{T}.ErrorCode"/>.</summary>
public sealed class DivinationDataSource : IDivinationDataSource
{

    private readonly DivinationService _divinationService;

    public DivinationDataSource(DivinationService divinationService)
    {

        _divinationService = divinationService;

    }

    public async Task<DataSourceResult<SemanticSearchResult>> DivineSessionsAsync(SemanticSearchRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SemanticSearchResult>? response = await _divinationService
            .SearchSessionsAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<SemanticSearchResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<WorkspaceSearchResult[]>? response = await _divinationService
            .SearchWorkspaceFilesAsync(workspaceId, request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<WorkspaceSearchResult[]>.FromResponse(response);

    }

    public async Task<DataSourceResult<SagaSearchResult>> DivineSagaAsync(SagaSearchRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<SagaSearchResult>? response = await _divinationService
            .SearchSagaAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<SagaSearchResult>.FromResponse(response);

    }

}
