using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;

/// <summary>Data-source seam for The Gatehouse.</summary>
public interface IGatehouseDataSource
{

    Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken);

    Task<bool> ResolveAsync(string wardId, bool allow, string? reason, CancellationToken cancellationToken);

}

/// <summary>API-backed Gatehouse data source.</summary>
public sealed class GatehouseDataSource : IGatehouseDataSource
{

    private readonly WardService _wardService;

    public GatehouseDataSource(WardService wardService)
    {

        _wardService = wardService;

    }

    public async Task<IReadOnlyList<WardDto>> ListWardsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<WardDto[]>? response = await _wardService.ListAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } wards } ? wards : [];

    }

    public async Task<bool> ResolveAsync(string wardId, bool allow, string? reason, CancellationToken cancellationToken)
    {

        ApiResponse<WardResolutionDto>? response = await _wardService
            .ResolveAsync(wardId, allow, reason, cancellationToken)
            .ConfigureAwait(false);

        return response is { IsSuccess: true };

    }

}
