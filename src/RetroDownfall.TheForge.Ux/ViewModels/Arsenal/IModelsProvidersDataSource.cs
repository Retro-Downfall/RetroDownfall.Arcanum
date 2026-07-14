using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>
/// Testable seam for the Models &amp; Providers operational view. Implementations forward to
/// <see cref="RetroDownfall.TheForge.Ux.Services.Services.ModelService"/> and map
/// <see cref="ApiResponse{T}"/> failures to null without throwing.
/// </summary>
public interface IModelsProvidersDataSource
{

    Task<IReadOnlyList<ModelInfoDto>> ListModelsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderInfoDto>> ListProvidersAsync(CancellationToken cancellationToken);

    Task<ProviderTestResult?> TestProviderAsync(ProviderTestRequest request, CancellationToken cancellationToken);

}
