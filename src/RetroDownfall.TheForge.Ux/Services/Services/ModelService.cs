using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/models</c> and <c>GET /api/providers</c>.</summary>
public sealed class ModelService
{

    private readonly ArcanumApiClient _apiClient;

    public ModelService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<ModelInfoDto[]>?> ListModelsAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/models", TheForgeJsonContext.Default.ApiResponseModelInfoDtoArray, cancellationToken);

    public Task<ApiResponse<ProviderInfoDto[]>?> ListProvidersAsync(CancellationToken cancellationToken) =>
        _apiClient.GetAsync("/api/providers", TheForgeJsonContext.Default.ApiResponseProviderInfoDtoArray, cancellationToken);

    public Task<ApiResponse<ProviderTestResult>?> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken) =>
        _apiClient.PostAsync("/api/providers/test", request,
            TheForgeJsonContext.Default.ProviderTestRequest,
            TheForgeJsonContext.Default.ApiResponseProviderTestResult, cancellationToken);

}
