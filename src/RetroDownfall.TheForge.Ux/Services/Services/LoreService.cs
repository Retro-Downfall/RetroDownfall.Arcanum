using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps the <c>/api/lore</c> route group for the Lore Browser.</summary>
public sealed class LoreService
{

    private readonly ArcanumApiClient _apiClient;

    public LoreService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<ListPageResult<LoreDto>>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/lore", ForgeJsonContext.Default.ApiResponseListPageResultLoreDto, cancellationToken);

    public Task<ApiResponse<LoreDto>?> GetAsync(string key, CancellationToken cancellationToken) =>
        _apiClient.GetAsync($"/api/lore/{Uri.EscapeDataString(key)}", ForgeJsonContext.Default.ApiResponseLoreDto, cancellationToken);

    public Task<ApiResponse<LoreDto>?> UpsertAsync(string key, string value, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/lore",
            new UpsertLoreRequest(key, value),
            ForgeJsonContext.Default.UpsertLoreRequest,
            ForgeJsonContext.Default.ApiResponseLoreDto,
            cancellationToken);

}
