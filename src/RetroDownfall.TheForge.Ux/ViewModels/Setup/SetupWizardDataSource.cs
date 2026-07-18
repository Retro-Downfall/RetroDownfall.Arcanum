using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Setup;

/// <summary>Production <see cref="ISetupWizardDataSource"/> over connection, settings, and route services.</summary>
public sealed class SetupWizardDataSource : ISetupWizardDataSource
{

    private readonly IArcanumConnection _connection;

    private readonly ITheForgeSettingsStore _settingsStore;

    private readonly IOptionsMonitor<TheForgeSettings> _settings;

    private readonly ITheForgeApiKeyProvider _apiKeyProvider;

    private readonly HealthService _healthService;

    private readonly ConfigService _configService;

    private readonly ModelService _modelService;

    public SetupWizardDataSource(
        IArcanumConnection connection,
        ITheForgeSettingsStore settingsStore,
        IOptionsMonitor<TheForgeSettings> settings,
        ITheForgeApiKeyProvider apiKeyProvider,
        HealthService healthService,
        ConfigService configService,
        ModelService modelService)
    {

        _connection = connection;

        _settingsStore = settingsStore;

        _settings = settings;

        _apiKeyProvider = apiKeyProvider;

        _healthService = healthService;

        _configService = configService;

        _modelService = modelService;

    }

    public string CurrentBaseUrl => _settings.CurrentValue.BaseUrl;

    public ConnectionState ConnectionState => _connection.State;

    public string? LastErrorCode => _connection.LastErrorCode;

    public string? LastErrorMessage => _connection.LastErrorMessage;

    public InstanceMetadataDto? LastMeta => _connection.LastMeta;

    public Task SaveBaseUrlAsync(string baseUrl, CancellationToken cancellationToken) =>
        _settingsStore.SavePatchAsync(s => s with { BaseUrl = baseUrl.Trim() }, cancellationToken);

    public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
        _apiKeyProvider.GetApiKeyAsync(cancellationToken);

    public Task PersistApiKeyAsync(string apiKey, CancellationToken cancellationToken) =>
        _apiKeyProvider.PersistPastedKeyAsync(apiKey, cancellationToken);

    public void ClearApiKeyPasteDecline() => _apiKeyProvider.ClearPasteDecline();

    public void Connect() => _connection.Connect();

    public void Disconnect() => _connection.Disconnect();

    public Task<ApiResponse<HealthReportDto>?> GetHealthAsync(CancellationToken cancellationToken) =>
        _healthService.GetHealthAsync(cancellationToken);

    public Task<ApiResponse<InstanceMetadataDto>?> GetMetaAsync(CancellationToken cancellationToken) =>
        _healthService.GetMetaAsync(cancellationToken);

    public Task<ApiResponse<ArcanumSettings>?> GetConfigAsync(CancellationToken cancellationToken) =>
        _configService.GetAsync(cancellationToken);

    public Task<ApiResponse<ModelInfoDto[]>?> ListModelsAsync(CancellationToken cancellationToken) =>
        _modelService.ListModelsAsync(cancellationToken);

    public Task<ApiResponse<ProviderInfoDto[]>?> ListProvidersAsync(CancellationToken cancellationToken) =>
        _modelService.ListProvidersAsync(cancellationToken);

}
