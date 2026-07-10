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
