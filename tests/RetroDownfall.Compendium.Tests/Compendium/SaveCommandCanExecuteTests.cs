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
public sealed class SaveCommandCanExecuteTests : IDisposable
{

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string _tempRoot;

    public SaveCommandCanExecuteTests()
    {

        _originalHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-save-canexec-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        Environment.SetEnvironmentVariable("HOME", _tempRoot);

        Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

    }

    [Fact]
    public async Task SaveCommand_CanExecute_requires_dirty_and_blocks_on_external_change()
    {

        await SeedAsync(new ArcanumSettings
        {
            Host = new HostSettings { Port = 5001 },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        Assert.False(vm.SaveCommand.CanExecute(null));

        int canExecuteChangedCount = 0;

        vm.SaveCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        vm.MarkDirty();

        Assert.True(vm.IsDirty);

        Assert.True(vm.SaveCommand.CanExecute(null));

        Assert.True(canExecuteChangedCount > 0);

        int afterDirty = canExecuteChangedCount;

        vm.HasExternalChange = true;

        Assert.False(vm.SaveCommand.CanExecute(null));

        Assert.True(canExecuteChangedCount > afterDirty);

    }

    [Fact]
    public async Task Editing_generic_field_value_enables_SaveCommand()
    {

        await SeedAsync(new ArcanumSettings
        {
            Host = new HostSettings { Port = 5001 },
            Features = new FeatureSettings { WebBrowsing = false },
        });

        ConfigurationViewModel vm = CreateViewModel();

        await WaitForLoadAsync(vm);

        Assert.False(vm.IsDirty);

        Assert.False(vm.SaveCommand.CanExecute(null));

        GenericSectionViewModel section = vm.GetOrCreateGenericSection(ConfigSection.Features);

        GenericSettingFieldViewModel? webBrowsing = section.Fields
            .FirstOrDefault(f => f.Descriptor.Key == "features.webBrowsing");

        Assert.NotNull(webBrowsing);

        int canExecuteChangedCount = 0;

        vm.SaveCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        webBrowsing.BoolValue = true;

        Assert.True(vm.IsDirty);

        Assert.True(vm.SaveCommand.CanExecute(null));

        Assert.True(canExecuteChangedCount > 0);

    }

    private static ConfigurationViewModel CreateViewModel()
    {

        ArcanumConfigurationStore store = new();

        return new ConfigurationViewModel(store, new NoopDialogService(), new SynchronousUiDispatcher());

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
