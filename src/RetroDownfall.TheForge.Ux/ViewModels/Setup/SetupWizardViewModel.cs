using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.ViewModels.Setup;

/// <summary>
/// First-run / connection setup wizard for The Forge. Guides Base URL → API key → connection test →
/// provider/model presence → default/FastModel → optional embeddings → Compendium. Does not edit
/// full <c>arcanum.json</c> (that remains Compendium's job).
/// </summary>
public sealed partial class SetupWizardViewModel : ViewModelBase
{

    private readonly ISetupWizardDataSource _dataSource;

    private readonly ICompendiumLauncher _compendiumLauncher;

    private readonly IWhispersService _whispers;

    [ObservableProperty]
    private SetupWizardStep _step = SetupWizardStep.BaseUrl;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _apiKeyInput = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private string? _providersSummary;

    [ObservableProperty]
    private string? _modelsSummary;

    [ObservableProperty]
    private string? _defaultModelSummary;

    [ObservableProperty]
    private string? _fastModelSummary;

    [ObservableProperty]
    private string? _embeddingsSummary;

    [ObservableProperty]
    private string? _configPathHint;

    [ObservableProperty]
    private string? _compendiumMessage;

    [ObservableProperty]
    private bool _connectionSucceeded;

    [ObservableProperty]
    private bool _hasProviders;

    [ObservableProperty]
    private bool _hasModels;

    [ObservableProperty]
    private bool _hasDefaultModel;

    [ObservableProperty]
    private bool _embeddingsEnabled;

    public SetupWizardViewModel(
        ISetupWizardDataSource dataSource,
        ICompendiumLauncher compendiumLauncher,
        IWhispersService whispers)
    {

        _dataSource = dataSource;

        _compendiumLauncher = compendiumLauncher;

        _whispers = whispers;

        Title = "Setup — The Forge";

        BaseUrl = dataSource.CurrentBaseUrl;

        ConfigPathHint = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

    }

    public string StepTitle => Step switch
    {
        SetupWizardStep.BaseUrl => "Arcanum base URL",
        SetupWizardStep.ApiKey => "API key",
        SetupWizardStep.TestConnection => "Test connection",
        SetupWizardStep.ProvidersAndModels => "Providers and models",
        SetupWizardStep.DefaultModel => "Default model",
        SetupWizardStep.Embeddings => "Embeddings (optional)",
        SetupWizardStep.Complete => "Ready",
        _ => "Setup",
    };

    public string StepDescription => Step switch
    {
        SetupWizardStep.BaseUrl =>
            "Enter the Arcanum HTTP base URL (default http://localhost:5001). When Arcanum listens on HTTPS only, use https://localhost:{HttpsPort}.",
        SetupWizardStep.ApiKey =>
            "Enter the master API key (X-Arcanum-Key). It is stored via The Forge's existing key provider — not written into local history files.",
        SetupWizardStep.TestConnection =>
            "The Forge will call Arcanum health/meta to verify the connection.",
        SetupWizardStep.ProvidersAndModels =>
            "Confirm at least one provider and model are configured on Arcanum.",
        SetupWizardStep.DefaultModel =>
            "Confirm DefaultModel / FastModel are visible from Arcanum configuration.",
        SetupWizardStep.Embeddings =>
            "Embeddings power Divination and The Weave. Optional for basic inference.",
        SetupWizardStep.Complete =>
            "Connection checks finished. Use Compendium for full arcanum.json editing.",
        _ => string.Empty,
    };

    public bool CanGoBack => Step is > SetupWizardStep.BaseUrl and < SetupWizardStep.Complete;

    public bool CanGoNext => Step < SetupWizardStep.Complete && !IsBusy;

    public bool IsComplete => Step == SetupWizardStep.Complete;

    partial void OnStepChanged(SetupWizardStep value)
    {

        OnPropertyChanged(nameof(StepTitle));

        OnPropertyChanged(nameof(StepDescription));

        OnPropertyChanged(nameof(CanGoBack));

        OnPropertyChanged(nameof(CanGoNext));

        OnPropertyChanged(nameof(IsComplete));

    }

    partial void OnIsBusyChanged(bool value)
    {

        OnPropertyChanged(nameof(CanGoNext));

    }

    [RelayCommand]
    private async Task BackAsync(CancellationToken cancellationToken)
    {

        if (!CanGoBack)
        {

            return;

        }

        ErrorText = null;

        Step = (SetupWizardStep)((int)Step - 1);

        await Task.CompletedTask.ConfigureAwait(true);

    }

    [RelayCommand]
    private async Task NextAsync(CancellationToken cancellationToken)
    {

        if (IsBusy)
        {

            return;

        }

        ErrorText = null;

        switch (Step)
        {

            case SetupWizardStep.BaseUrl:
                await AdvanceFromBaseUrlAsync(cancellationToken).ConfigureAwait(true);
                break;

            case SetupWizardStep.ApiKey:
                await AdvanceFromApiKeyAsync(cancellationToken).ConfigureAwait(true);
                break;

            case SetupWizardStep.TestConnection:
                await AdvanceFromTestConnectionAsync(cancellationToken).ConfigureAwait(true);
                break;

            case SetupWizardStep.ProvidersAndModels:
                await AdvanceFromProvidersAsync(cancellationToken).ConfigureAwait(true);
                break;

            case SetupWizardStep.DefaultModel:
                await AdvanceFromDefaultModelAsync(cancellationToken).ConfigureAwait(true);
                break;

            case SetupWizardStep.Embeddings:
                await AdvanceFromEmbeddingsAsync(cancellationToken).ConfigureAwait(true);
                break;

        }

    }

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {

        await RunConnectionProbeAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    private void OpenCompendium()
    {

        CompendiumLaunchResult result = _compendiumLauncher.TryLaunch();

        CompendiumMessage = result.Message;

        ConfigPathHint = result.ConfigPath;

        if (result.Launched)
        {

            _whispers.Show(WhisperSeverity.Success, "Opened Compendium.");

        }
        else
        {

            _whispers.Show(WhisperSeverity.Warning, result.Message);

        }

    }

    [RelayCommand]
    private void SkipEmbeddings()
    {

        EmbeddingsSummary = "Embeddings step skipped — Divination / The Weave may stay disabled until enabled in Compendium.";

        Step = SetupWizardStep.Complete;

        StatusText = "Setup complete (embeddings skipped).";

    }

    private async Task AdvanceFromBaseUrlAsync(CancellationToken cancellationToken)
    {

        string trimmed = BaseUrl.Trim();

        if (string.IsNullOrWhiteSpace(trimmed)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {

            ErrorText = "Enter an absolute http:// or https:// Base URL.";

            return;

        }

        IsBusy = true;

        try
        {

            await _dataSource.SaveBaseUrlAsync(trimmed, cancellationToken).ConfigureAwait(true);

            BaseUrl = trimmed;

            StatusText = "Base URL saved to The Forge settings.";

            Step = SetupWizardStep.ApiKey;

        }
        catch (Exception ex)
        {

            ErrorText = $"Failed to save Base URL: {ex.Message}";

        }
        finally
        {

            IsBusy = false;

        }

    }

    private async Task AdvanceFromApiKeyAsync(CancellationToken cancellationToken)
    {

        string? existing = await _dataSource.GetApiKeyAsync(cancellationToken).ConfigureAwait(true);

        string candidate = string.IsNullOrWhiteSpace(ApiKeyInput) ? existing ?? string.Empty : ApiKeyInput.Trim();

        if (string.IsNullOrWhiteSpace(candidate))
        {

            ErrorText = "Paste an API key, or ensure one is already stored in the OS keychain.";

            return;

        }

        IsBusy = true;

        try
        {

            if (!string.IsNullOrWhiteSpace(ApiKeyInput))
            {

                await _dataSource.PersistApiKeyAsync(ApiKeyInput.Trim(), cancellationToken).ConfigureAwait(true);

                ApiKeyInput = string.Empty;

            }

            _dataSource.ClearApiKeyPasteDecline();

            StatusText = "API key ready (not written to history files).";

            Step = SetupWizardStep.TestConnection;

        }
        catch (Exception ex)
        {

            ErrorText = $"Failed to store API key: {ex.Message}";

        }
        finally
        {

            IsBusy = false;

        }

    }

    private async Task AdvanceFromTestConnectionAsync(CancellationToken cancellationToken)
    {

        await RunConnectionProbeAsync(cancellationToken).ConfigureAwait(true);

        if (ConnectionSucceeded)
        {

            Step = SetupWizardStep.ProvidersAndModels;

        }

    }

    private async Task AdvanceFromProvidersAsync(CancellationToken cancellationToken)
    {

        await RefreshProvidersAndModelsAsync(cancellationToken).ConfigureAwait(true);

        if (!HasProviders)
        {

            ErrorText = "No providers configured. Open Compendium to add a provider, then retry.";

            return;

        }

        if (!HasModels)
        {

            ErrorText = "No models listed. Configure models in Compendium, then retry.";

            return;

        }

        Step = SetupWizardStep.DefaultModel;

    }

    private async Task AdvanceFromDefaultModelAsync(CancellationToken cancellationToken)
    {

        await RefreshDefaultModelsAsync(cancellationToken).ConfigureAwait(true);

        if (!HasDefaultModel)
        {

            ErrorText = "DefaultModel is empty. Set DefaultModel (and optionally FastModel) in Compendium.";

            return;

        }

        Step = SetupWizardStep.Embeddings;

    }

    private async Task AdvanceFromEmbeddingsAsync(CancellationToken cancellationToken)
    {

        await RefreshEmbeddingsAsync(cancellationToken).ConfigureAwait(true);

        Step = SetupWizardStep.Complete;

        StatusText = EmbeddingsEnabled
            ? "Setup complete — embeddings enabled."
            : "Setup complete — embeddings disabled (optional).";

    }

    private async Task RunConnectionProbeAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        ConnectionSucceeded = false;

        ErrorText = null;

        StatusText = "Connecting…";

        try
        {

            _dataSource.ClearApiKeyPasteDecline();

            _dataSource.Connect();

            ApiResponse<HealthReportDto>? health = await _dataSource.GetHealthAsync(cancellationToken).ConfigureAwait(true);

            if (health is null)
            {

                ErrorText = "No response from Arcanum. Check Base URL and that Arcanum is running.";

                StatusText = "Connection failed.";

                return;

            }

            if (!health.IsSuccess)
            {

                ErrorText = health.Error?.Message
                    ?? _dataSource.LastErrorMessage
                    ?? $"Health check failed ({health.Error?.Code ?? _dataSource.LastErrorCode ?? "unknown"}).";

                StatusText = "Connection failed.";

                return;

            }

            ApiResponse<InstanceMetadataDto>? meta = await _dataSource.GetMetaAsync(cancellationToken).ConfigureAwait(true);

            if (meta is { IsSuccess: true, Data: { } metaData })
            {

                ConfigPathHint = metaData.ConfigPath;

            }

            ConnectionSucceeded = true;

            StatusText = "Connected to Arcanum.";

            await RefreshProvidersAndModelsAsync(cancellationToken).ConfigureAwait(true);

            await RefreshDefaultModelsAsync(cancellationToken).ConfigureAwait(true);

            await RefreshEmbeddingsAsync(cancellationToken).ConfigureAwait(true);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            StatusText = "Cancelled.";

        }
        catch (Exception ex)
        {

            ErrorText = ex.Message;

            StatusText = "Connection failed.";

        }
        finally
        {

            IsBusy = false;

        }

    }

    private async Task RefreshProvidersAndModelsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ProviderInfoDto[]>? providers = await _dataSource.ListProvidersAsync(cancellationToken).ConfigureAwait(true);

        ProviderInfoDto[] providerList = providers is { IsSuccess: true, Data: { } p } ? p : [];

        HasProviders = providerList.Length > 0;

        ProvidersSummary = HasProviders
            ? $"{providerList.Length} provider(s): {string.Join(", ", providerList.Select(static x => x.Name).Take(5))}."
            : "No providers returned from GET /api/providers.";

        ApiResponse<ModelInfoDto[]>? models = await _dataSource.ListModelsAsync(cancellationToken).ConfigureAwait(true);

        ModelInfoDto[] modelList = models is { IsSuccess: true, Data: { } m } ? m : [];

        HasModels = modelList.Length > 0;

        ModelsSummary = HasModels
            ? $"{modelList.Length} model(s) listed."
            : "No models returned from GET /api/models.";

    }

    private async Task RefreshDefaultModelsAsync(CancellationToken cancellationToken)
    {

        ApiResponse<ArcanumSettings>? config = await _dataSource.GetConfigAsync(cancellationToken).ConfigureAwait(true);

        if (config is not { IsSuccess: true, Data: { } settings })
        {

            HasDefaultModel = false;

            DefaultModelSummary = "Could not read Arcanum configuration (GET /api/config).";

            FastModelSummary = null;

            return;

        }

        HasDefaultModel = !string.IsNullOrWhiteSpace(settings.DefaultModel);

        DefaultModelSummary = HasDefaultModel
            ? $"DefaultModel: {settings.DefaultModel}"
            : "DefaultModel is not set.";

        FastModelSummary = string.IsNullOrWhiteSpace(settings.FastModel)
            ? "FastModel is not set (optional)."
            : $"FastModel: {settings.FastModel}";

    }

    private async Task RefreshEmbeddingsAsync(CancellationToken cancellationToken)
    {

        InstanceMetadataDto? meta = _dataSource.LastMeta;

        if (meta is null)
        {

            ApiResponse<InstanceMetadataDto>? response = await _dataSource.GetMetaAsync(cancellationToken).ConfigureAwait(true);

            meta = response is { IsSuccess: true, Data: { } data } ? data : null;

        }

        EmbeddingsEnabled = meta?.EmbeddingsEnabled ?? false;

        EmbeddingsSummary = meta is null
            ? "Could not read embeddings status from /api/meta."
            : EmbeddingsEnabled
                ? $"Embeddings enabled (vector mode: {meta.EmbeddingsVectorMode})."
                : "Embeddings disabled — enable Arcanum:Features:Embeddings in Compendium for Divination / The Weave.";

        if (meta is not null && !string.IsNullOrWhiteSpace(meta.ConfigPath))
        {

            ConfigPathHint = meta.ConfigPath;

        }

    }

}
