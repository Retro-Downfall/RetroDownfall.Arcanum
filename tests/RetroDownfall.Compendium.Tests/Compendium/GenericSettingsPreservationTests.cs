using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class GenericSettingsPreservationTests : IDisposable
{

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string _tempRoot;

    public GenericSettingsPreservationTests()
    {

        _originalHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-generic-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        Environment.SetEnvironmentVariable("HOME", _tempRoot);

        Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

    }

    [Fact]
    public async Task Editing_polished_host_preserves_generic_resilience_values()
    {

        await SeedAsync(new ArcanumSettings
        {
            Host = new HostSettings { Port = 5001 },
            Resilience = new ResilienceSettings
            {
                Enabled = true,
                HealthProbeIntervalSeconds = 42,
                MaxFallbackAttempts = 3,
            },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        Assert.Equal(5001, vm.Host.Port);

        vm.Host.Port = 5100;

        ArcanumSettings built = vm.BuildSettings();

        Assert.Equal(5100, built.Host.Port);

        Assert.True(built.Resilience.Enabled);

        Assert.Equal(42, built.Resilience.HealthProbeIntervalSeconds);

        Assert.Equal(3, built.Resilience.MaxFallbackAttempts);

    }

    [Fact]
    public async Task Editing_generic_resilience_field_persists_on_build()
    {

        await SeedAsync(new ArcanumSettings
        {
            Host = new HostSettings { Port = 5001 },
            Resilience = new ResilienceSettings
            {
                Enabled = false,
                HealthProbeIntervalSeconds = 30,
            },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        GenericSectionViewModel section = vm.GetOrCreateGenericSection(ConfigSection.Resilience);

        GenericSettingFieldViewModel? interval = section.Fields
            .FirstOrDefault(f => f.Descriptor.Key == "resilience.healthProbeIntervalSeconds");

        Assert.NotNull(interval);

        interval.NumericValue = 55;

        GenericSettingFieldViewModel? enabled = section.Fields
            .FirstOrDefault(f => f.Descriptor.Key == "resilience.enabled");

        Assert.NotNull(enabled);

        enabled.BoolValue = true;

        ArcanumSettings built = vm.BuildSettings();

        Assert.True(built.Resilience.Enabled);

        Assert.Equal(55, built.Resilience.HealthProbeIntervalSeconds);

        Assert.Equal(5001, built.Host.Port);

    }

    [Fact]
    public async Task Nullable_reasoning_price_preserves_null_and_can_set_then_clear_zero()
    {
        await SeedAsync(new ArcanumSettings
        {
            Pricing = new PricingSettings
            {
                DefaultPricing = new ModelPricingEntry
                {
                    OutputPer1M = 5m,
                    ReasoningPer1M = null,
                },
            },
        });

        ConfigurationViewModel vm = CreateViewModel();
        await WaitForLoadAsync(vm);
        GenericSectionViewModel section = vm.GetOrCreateGenericSection(ConfigSection.Pricing);
        GenericSettingFieldViewModel reasoning = Assert.Single(
            section.Fields,
            static field => field.Descriptor.Key == "pricing.defaultPricing.reasoningPer1M");

        Assert.True(reasoning.Descriptor.AllowUnset);
        Assert.False(reasoning.IsSet);
        Assert.Null(vm.BuildSettings().Pricing.DefaultPricing.ReasoningPer1M);

        reasoning.IsSet = true;
        reasoning.NumericValue = 0;
        Assert.Equal(0m, vm.BuildSettings().Pricing.DefaultPricing.ReasoningPer1M);

        reasoning.IsSet = false;
        ArcanumSettings cleared = vm.BuildSettings();
        Assert.Null(cleared.Pricing.DefaultPricing.ReasoningPer1M);

        using ArcanumConfigurationStore store = new(new ArcanumDataProtectionSecretProtector());
        ConfigurationWriteResult writeResult = await store.WriteAsync(cleared, CancellationToken.None);
        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);
        ArcanumSettings saved = await store.ReadAsync(CancellationToken.None);
        Assert.Null(saved.Pricing.DefaultPricing.ReasoningPer1M);
    }

    [Fact]
    public async Task Provider_reasoning_capabilities_survive_load_build_and_save_round_trip()
    {

        ReasoningCapabilities expected = new()
        {
            ControlSupport = ReasoningControlSupport.EffortAndBudget,
            SupportsSummary = true,
            SupportsFull = true,
            SupportsStreaming = true,
            ReportsReasoningTokens = true,
            AllowsClientOutput = true,
            WireDialect = ReasoningWireDialect.OpenRouter,
            MaxBudgetTokens = 32768,
        };

        await SeedAsync(new ArcanumSettings
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "reasoning-provider",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://reasoning.example/v1",
                    Models = [new ModelEntry("reasoner", SupportsVision: true, Reasoning: expected)],
                },
            ],
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        ProvidersSectionViewModel.ModelEntryViewModel model = Assert.Single(
            Assert.Single(vm.Providers.Providers).Models);

        Assert.Equal(expected, model.Reasoning);

        ArcanumSettings built = vm.BuildSettings();
        ReasoningCapabilities builtReasoning = Assert.IsType<ReasoningCapabilities>(
            Assert.Single(Assert.Single(built.Providers).Models).Reasoning);

        Assert.Equal(expected, builtReasoning);

        using ArcanumConfigurationStore store = new(new ArcanumDataProtectionSecretProtector());
        ConfigurationWriteResult writeResult = await store.WriteAsync(built, CancellationToken.None);

        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings saved = await store.ReadAsync(CancellationToken.None);
        ReasoningCapabilities savedReasoning = Assert.IsType<ReasoningCapabilities>(
            Assert.Single(Assert.Single(saved.Providers).Models).Reasoning);

        Assert.Equal(expected, savedReasoning);

    }

    [Fact]
    public async Task Provider_tokenization_profiles_survive_load_build_and_save_round_trip()
    {
        ModelTokenizationProfile expected = new()
        {
            Type = ModelTokenizationProfileType.CalibratedApproximation,
            TokenizerId = "o200k_base",
            SafetyMarginPercent = 20,
            UnknownImageReserveTokens = 4096,
            Confidence = 0.8,
        };

        await SeedAsync(new ArcanumSettings
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "profiled-provider",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://profiled.example/v1",
                    Models = [new ModelEntry("profiled-model", Tokenization: expected)],
                },
            ],
        });

        ConfigurationViewModel vm = CreateViewModel();
        await WaitForLoadAsync(vm);

        ProvidersSectionViewModel.ModelEntryViewModel model = Assert.Single(
            Assert.Single(vm.Providers.Providers).Models);
        Assert.Equal(expected, model.Tokenization);

        ArcanumSettings built = vm.BuildSettings();
        Assert.Equal(
            expected,
            Assert.Single(Assert.Single(built.Providers).Models).Tokenization);

        using ArcanumConfigurationStore store = new(new ArcanumDataProtectionSecretProtector());
        ConfigurationWriteResult writeResult = await store.WriteAsync(built, CancellationToken.None);
        Assert.True(writeResult.IsSuccess, writeResult.ErrorMessage);

        ArcanumSettings saved = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(
            expected,
            Assert.Single(Assert.Single(saved.Providers).Models).Tokenization);
    }

    private static ConfigurationViewModel CreateViewModel()
    {

        ArcanumDataProtectionSecretProtector protector = new();

        ArcanumConfigurationStore store = new(protector);

        return new ConfigurationViewModel(store, new NoopDialogService(), new SynchronousUiDispatcher());

    }

    private static async Task SeedAsync(ArcanumSettings settings)
    {

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        ArcanumDataProtectionSecretProtector protector = new();

        ArcanumSettings encrypted = protector.EncryptProviderKeys(settings);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new ArcanumConfigurationFile { Arcanum = encrypted },
                ConfigurationJsonContext.Default.ArcanumConfigurationFile));

    }

    private static async Task WaitForLoadAsync(ConfigurationViewModel vm)
    {

        for (int i = 0; i < 50; i++)
        {

            if (!string.IsNullOrEmpty(vm.StatusMessage) && vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal))
            {

                return;

            }

            await Task.Delay(50);

        }

        Assert.Fail($"Timed out waiting for load. Status={vm.StatusMessage}");

    }

    public void Dispose()
    {

        Environment.SetEnvironmentVariable("HOME", _originalHome);

        Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

        try
        {

            if (Directory.Exists(_tempRoot))
            {

                Directory.Delete(_tempRoot, recursive: true);

            }

        }
        catch
        {
            // best-effort cleanup
        }

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
            => Task.FromResult(true);

    }

}
