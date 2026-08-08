using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class CancelCommandTests : IDisposable
{

    private readonly ArcanumTestHomeScope _home;

    private readonly string _tempRoot;

    public CancelCommandTests()
    {

        _home = new ArcanumTestHomeScope("compendium-cancel");

        _tempRoot = _home.Root;

    }

    [Fact]
    public async Task CancelCommand_discards_local_edits_and_clears_dirty()
    {

        await SeedAsync(new ArcanumSettings
        {
            Host = new HostSettings
            {
                Port = 5001,
                CorsAllowedOrigins =
                [
                    "http://localhost:5001",
                    "http://127.0.0.1:5001",
                ],
            },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        Assert.Equal(5001, vm.Host.Port);

        Assert.False(vm.IsDirty);

        vm.Host.Port = 5100;

        vm.Host.CorsAllowedOrigins = "http://localhost:5001";

        Assert.True(vm.IsDirty);

        Assert.True(vm.CancelCommand.CanExecute(null));

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);

        Assert.Equal(5001, vm.Host.Port);

        Assert.Equal("http://localhost:5001, http://127.0.0.1:5001", vm.Host.CorsAllowedOrigins);

        Assert.False(vm.CancelCommand.CanExecute(null));

    }

    private static ConfigurationViewModel CreateViewModel()
    {

        ArcanumConfigurationStore store = new();

        return new ConfigurationViewModel(
            store,
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance);

    }

    private static async Task SeedAsync(ArcanumSettings settings)
    {

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string configPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        await File.WriteAllTextAsync(
            configPath,
            JsonSerializer.Serialize(
                new ArcanumConfigurationFile { Arcanum = settings },
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

    public void Dispose() => _home.Dispose();

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
            => Task.FromResult(true);

    }

}
