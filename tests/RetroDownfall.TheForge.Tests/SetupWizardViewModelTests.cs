using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services.Compendium;
using RetroDownfall.TheForge.Ux.ViewModels.Setup;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class SetupWizardViewModelTests
{

    [Fact]
    public async Task Next_FromBaseUrl_SavesAndAdvancesToApiKey()
    {

        FakeSetupWizardDataSource data = new() { CurrentBaseUrl = "http://localhost:5001" };

        SetupWizardViewModel vm = Create(data);

        vm.BaseUrl = "http://localhost:5002";

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.ApiKey, vm.Step);

        Assert.Equal("http://localhost:5002", data.SavedBaseUrl);

        Assert.Null(vm.ErrorText);

    }

    [Fact]
    public async Task Next_FromBaseUrl_RejectsInvalidUrl()
    {

        SetupWizardViewModel vm = Create(new FakeSetupWizardDataSource());

        vm.BaseUrl = "not-a-url";

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.BaseUrl, vm.Step);

        Assert.Contains("http://", vm.ErrorText, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Next_FromApiKey_PersistsKeyAndClearsInput()
    {

        FakeSetupWizardDataSource data = new();

        SetupWizardViewModel vm = Create(data);

        vm.Step = SetupWizardStep.ApiKey;

        vm.ApiKeyInput = "secret-key";

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.TestConnection, vm.Step);

        Assert.Equal("secret-key", data.PersistedApiKey);

        Assert.Equal(string.Empty, vm.ApiKeyInput);

        Assert.True(data.PasteDeclineCleared);

    }

    [Fact]
    public async Task Next_FromApiKey_WithoutKey_ShowsError()
    {

        SetupWizardViewModel vm = Create(new FakeSetupWizardDataSource());

        vm.Step = SetupWizardStep.ApiKey;

        vm.ApiKeyInput = string.Empty;

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.ApiKey, vm.Step);

        Assert.Contains("API key", vm.ErrorText, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task TestConnection_Success_AdvancesProvidersOnNext()
    {

        FakeSetupWizardDataSource data = new()
        {
            Health = new ApiResponse<HealthReportDto>(new HealthReportDto(HealthStatus.Healthy, []), true, null),
            Meta = new ApiResponse<InstanceMetadataDto>(SampleMeta(embeddingsEnabled: true), true, null),
            Providers = new ApiResponse<ProviderInfoDto[]>(
                [new ProviderInfoDto("local", "OpenAICompatible", "http://x", null, ["m"], 8)],
                true,
                null),
            Models = new ApiResponse<ModelInfoDto[]>(
                [new ModelInfoDto("m", "local", "OpenAICompatible", "http://x", 8, false)],
                true,
                null),
            Config = new ApiResponse<ArcanumSettings>(
                new ArcanumSettings { DefaultModel = "m", FastModel = "m-fast" },
                true,
                null),
        };

        SetupWizardViewModel vm = Create(data);

        vm.Step = SetupWizardStep.TestConnection;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(vm.ConnectionSucceeded);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.ProvidersAndModels, vm.Step);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.DefaultModel, vm.Step);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.Embeddings, vm.Step);

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(SetupWizardStep.Complete, vm.Step);

        Assert.True(vm.EmbeddingsEnabled);

    }

    [Fact]
    public void OpenCompendium_SurfacesLauncherMessage()
    {

        FakeCompendiumLauncher launcher = new() { LaunchSucceeded = false };

        SetupWizardViewModel vm = Create(new FakeSetupWizardDataSource(), launcher);

        vm.OpenCompendiumCommand.Execute(null);

        Assert.Equal(1, launcher.LaunchCount);

        Assert.Contains("arcanum.json", vm.CompendiumMessage, StringComparison.Ordinal);

    }

    [Fact]
    public void SkipEmbeddings_CompletesWizard()
    {

        SetupWizardViewModel vm = Create(new FakeSetupWizardDataSource());

        vm.Step = SetupWizardStep.Embeddings;

        vm.SkipEmbeddingsCommand.Execute(null);

        Assert.Equal(SetupWizardStep.Complete, vm.Step);

    }

    private static SetupWizardViewModel Create(
        ISetupWizardDataSource data,
        ICompendiumLauncher? launcher = null) =>
        new(data, launcher ?? new FakeCompendiumLauncher(), new FakeWhispersService());

    private static InstanceMetadataDto SampleMeta(bool embeddingsEnabled) =>
        new(
            "1.0",
            "os",
            "rid",
            1,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            false,
            "/tmp",
            "/tmp/arcanum.json",
            5001,
            false,
            false,
            false,
            false,
            false,
            false,
            0,
            null,
            null,
            embeddingsEnabled,
            "managed",
            "ok",
            100,
            "local",
            false);

    private sealed class FakeSetupWizardDataSource : ISetupWizardDataSource
    {

        public string CurrentBaseUrl { get; set; } = "http://localhost:5001";

        public string? SavedBaseUrl { get; private set; }

        public string? PersistedApiKey { get; private set; }

        public bool PasteDeclineCleared { get; private set; }

        public ConnectionState ConnectionState { get; set; } = ConnectionState.Disconnected;

        public string? LastErrorCode { get; set; }

        public string? LastErrorMessage { get; set; }

        public InstanceMetadataDto? LastMeta { get; set; }

        public ApiResponse<HealthReportDto>? Health { get; set; }

        public ApiResponse<InstanceMetadataDto>? Meta { get; set; }

        public ApiResponse<ArcanumSettings>? Config { get; set; }

        public ApiResponse<ModelInfoDto[]>? Models { get; set; }

        public ApiResponse<ProviderInfoDto[]>? Providers { get; set; }

        public Task SaveBaseUrlAsync(string baseUrl, CancellationToken cancellationToken)
        {

            SavedBaseUrl = baseUrl;

            CurrentBaseUrl = baseUrl;

            return Task.CompletedTask;

        }

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(PersistedApiKey);

        public Task PersistApiKeyAsync(string apiKey, CancellationToken cancellationToken)
        {

            PersistedApiKey = apiKey;

            return Task.CompletedTask;

        }

        public void ClearApiKeyPasteDecline() => PasteDeclineCleared = true;

        public void Connect() => ConnectionState = ConnectionState.Connected;

        public void Disconnect() => ConnectionState = ConnectionState.Disconnected;

        public Task<ApiResponse<HealthReportDto>?> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Health);

        public Task<ApiResponse<InstanceMetadataDto>?> GetMetaAsync(CancellationToken cancellationToken)
        {

            if (Meta is { IsSuccess: true, Data: { } data })
            {

                LastMeta = data;

            }

            return Task.FromResult(Meta);

        }

        public Task<ApiResponse<ArcanumSettings>?> GetConfigAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Config);

        public Task<ApiResponse<ModelInfoDto[]>?> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Models);

        public Task<ApiResponse<ProviderInfoDto[]>?> ListProvidersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Providers);

    }

}
