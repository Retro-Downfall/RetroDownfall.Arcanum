using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps the <c>/api/saga</c> route group for The Archive (saga memory browser).</summary>
public sealed class SagaService
{

    private readonly ArcanumApiClient _apiClient;

    public SagaService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<SagaMemoryDto[]>?> ListAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/saga", ForgeJsonContext.Default.ApiResponseSagaMemoryDtoArray, cancellationToken);

    public Task<ApiResponse<SagaSearchResult>?> SearchAsync(string query, int? limit, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/saga/divine",
            new SagaSearchRequest(query, limit),
            ForgeJsonContext.Default.SagaSearchRequest,
            ForgeJsonContext.Default.ApiResponseSagaSearchResult,
            cancellationToken);

}
