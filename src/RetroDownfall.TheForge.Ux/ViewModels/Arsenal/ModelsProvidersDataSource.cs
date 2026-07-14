using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>API-backed <see cref="IModelsProvidersDataSource"/> — wraps <see cref="ModelService"/>.</summary>
public sealed class ModelsProvidersDataSource : IModelsProvidersDataSource
{

    private readonly ModelService _modelService;

    public ModelsProvidersDataSource(ModelService modelService)
    {

        _modelService = modelService;

    }

    public async Task<IReadOnlyList<ModelInfoDto>> ListModelsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ModelInfoDto[]>? response = await _modelService.ListModelsAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } models } ? models : [];

    }

    public async Task<IReadOnlyList<ProviderInfoDto>> ListProvidersAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ProviderInfoDto[]>? response = await _modelService.ListProvidersAsync(cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } providers } ? providers : [];

    }

    public async Task<ProviderTestResult?> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken)
    {

        ApiResponse<ProviderTestResult>? response = await _modelService.TestProviderAsync(request, cancellationToken).ConfigureAwait(false);

        return response is { IsSuccess: true, Data: { } result } ? result : null;

    }

}
