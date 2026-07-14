using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Archive;

/// <summary>Data-source seam for The Archive (Saga memory); tests fake this interface.</summary>
public interface ISagaArchiveDataSource
{

    Task<DataSourceResult<SagaMemoryDto[]>> ListAsync(string? query, Guid? sessionId, int? limit, int? offset, CancellationToken cancellationToken);

    Task<DataSourceResult<SagaSearchResult>> DivineAsync(string query, int? limit, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> DeleteAsync(string id, CancellationToken cancellationToken);

    Task<DataSourceResult<SagaStats>> GetStatsAsync(CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="ISagaArchiveDataSource"/> — wraps <see cref="SagaService"/>. List/stats/delete are always available; Divination surfaces <c>Embeddings.FeatureDisabled</c> via <see cref="DataSourceResult{T}.ErrorCode"/>.</summary>
public sealed class SagaArchiveDataSource : ISagaArchiveDataSource
{

    private readonly SagaService _sagaService;

    public SagaArchiveDataSource(SagaService sagaService)
    {

        _sagaService = sagaService;

    }

    public async Task<DataSourceResult<SagaMemoryDto[]>> ListAsync(string? query, Guid? sessionId, int? limit, int? offset, CancellationToken cancellationToken)
    {

        ApiResponse<SagaMemoryDto[]>? response = await _sagaService
            .ListAsync(query, sessionId, limit, offset, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<SagaMemoryDto[]>.FromResponse(response);

    }

    public async Task<DataSourceResult<SagaSearchResult>> DivineAsync(string query, int? limit, CancellationToken cancellationToken)
    {

        ApiResponse<SagaSearchResult>? response = await _sagaService
            .SearchAsync(query, limit, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<SagaSearchResult>.FromResponse(response);

    }

    public async Task<DataSourceResult<bool>> DeleteAsync(string id, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _sagaService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<bool>.FromResponse(response);

    }

    public async Task<DataSourceResult<SagaStats>> GetStatsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<SagaStats>? response = await _sagaService.GetStatsAsync(cancellationToken).ConfigureAwait(false);

        return DataSourceResult<SagaStats>.FromResponse(response);

    }

}
