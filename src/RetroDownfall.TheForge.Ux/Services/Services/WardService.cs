using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the <c>/api/wards</c> route group for The Gatehouse. A single
/// <c>POST /api/wards/{id}</c> resolves a ward — there is no separate approve/deny route; the
/// distinction is <see cref="ResolveWardRequest.Allow"/>.
/// </summary>
public sealed class WardService
{

    private readonly ArcanumApiClient _apiClient;

    public WardService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<WardDto[]>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/wards", ForgeJsonContext.Default.ApiResponseWardDtoArray, cancellationToken);

    public Task<ApiResponse<WardResolutionDto>?> ResolveAsync(string wardId, bool allow, string? reason, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            $"/api/wards/{Uri.EscapeDataString(wardId)}",
            new ResolveWardRequest(allow, reason),
            ForgeJsonContext.Default.ResolveWardRequest,
            ForgeJsonContext.Default.ApiResponseWardResolutionDto,
            cancellationToken);

}
