using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Lore;

/// <summary>Data-source seam for the Lore Browser; tests fake this interface.</summary>
public interface ILoreDataSource
{

    Task<DataSourceResult<ListPageResult<LoreDto>>> ListAsync(CancellationToken cancellationToken);

    Task<DataSourceResult<LoreDto>> GetAsync(string key, CancellationToken cancellationToken);

    Task<DataSourceResult<LoreDto>> UpsertAsync(string key, string value, CancellationToken cancellationToken);

    Task<DataSourceResult<bool>> DeleteAsync(string key, CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="ILoreDataSource"/> — wraps <see cref="LoreService"/> and keeps the server <c>Error.Code</c> on failure.</summary>
public sealed class LoreDataSource : ILoreDataSource
{

    private readonly LoreService _loreService;

    public LoreDataSource(LoreService loreService)
    {

        _loreService = loreService;

    }

    public async Task<DataSourceResult<ListPageResult<LoreDto>>> ListAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ListPageResult<LoreDto>>? response = await _loreService.ListAsync(cancellationToken).ConfigureAwait(false);

        return DataSourceResult<ListPageResult<LoreDto>>.FromResponse(response);

    }

    public async Task<DataSourceResult<LoreDto>> GetAsync(string key, CancellationToken cancellationToken)
    {

        ApiResponse<LoreDto>? response = await _loreService.GetAsync(key, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<LoreDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<LoreDto>> UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {

        ApiResponse<LoreDto>? response = await _loreService.UpsertAsync(key, value, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<LoreDto>.FromResponse(response);

    }

    public async Task<DataSourceResult<bool>> DeleteAsync(string key, CancellationToken cancellationToken)
    {

        ApiResponse<bool>? response = await _loreService.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

        return DataSourceResult<bool>.FromResponse(response);

    }

}
