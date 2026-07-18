using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Setup;

/// <summary>API/settings seam for <see cref="SetupWizardViewModel"/> (faked in tests).</summary>
public interface ISetupWizardDataSource
{

    string CurrentBaseUrl { get; }

    Task SaveBaseUrlAsync(string baseUrl, CancellationToken cancellationToken);

    Task<string?> GetApiKeyAsync(CancellationToken cancellationToken);

    Task PersistApiKeyAsync(string apiKey, CancellationToken cancellationToken);

    void ClearApiKeyPasteDecline();

    void Connect();

    void Disconnect();

    ConnectionState ConnectionState { get; }

    string? LastErrorCode { get; }

    string? LastErrorMessage { get; }

    InstanceMetadataDto? LastMeta { get; }

    Task<ApiResponse<HealthReportDto>?> GetHealthAsync(CancellationToken cancellationToken);

    Task<ApiResponse<InstanceMetadataDto>?> GetMetaAsync(CancellationToken cancellationToken);

    Task<ApiResponse<ArcanumSettings>?> GetConfigAsync(CancellationToken cancellationToken);

    Task<ApiResponse<ModelInfoDto[]>?> ListModelsAsync(CancellationToken cancellationToken);

    Task<ApiResponse<ProviderInfoDto[]>?> ListProvidersAsync(CancellationToken cancellationToken);

}
