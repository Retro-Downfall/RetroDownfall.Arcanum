using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// Editing one field must cost one dirty pass and republish the pointer-keyed error map only when the
/// error set actually changed. Every generic field raises several derived notifications per edit, and
/// relaying all of them ran a full sweep of every field of every opened section — and pushed a brand new
/// dictionary to every bound control — for each one.
/// </summary>
public sealed class ConfigurationViewModelNotificationTests
{

    [Fact]
    public async Task Editing_a_valid_field_does_not_republish_the_validation_map()
    {

        ConfigurationViewModel vm = await CreateLoadedViewModelAsync();

        OpenEverySection(vm);

        GenericSettingFieldViewModel field = Field(vm, ConfigSection.Features, "features.webBrowsing");

        // Settle IsDirty first so the counters below observe steady-state churn only.
        field.BoolValue = !field.BoolValue;

        int republished = 0;

        vm.PropertyChanged += (_, args) =>
        {

            if (args.PropertyName == nameof(ConfigurationViewModel.ValidationErrorsByPointer))
            {

                republished++;

            }

        };

        int dirtyPasses = 0;

        vm.SaveCommand.CanExecuteChanged += (_, _) => dirtyPasses++;

        field.BoolValue = !field.BoolValue;

        Assert.False(vm.HasFieldErrors);

        Assert.Equal(0, republished);

        Assert.True(
            dirtyPasses <= 2,
            $"One field edit ran {dirtyPasses} validation sweeps; the derived projections must not each trigger one.");

    }

    [Fact]
    public async Task An_error_appearing_and_clearing_republishes_the_validation_map_exactly_once()
    {

        ConfigurationViewModel vm = await CreateLoadedViewModelAsync();

        OpenEverySection(vm);

        GenericSettingFieldViewModel field =
            Field(vm, ConfigSection.Retention, "retention.protectedSessionIds");

        int republished = 0;

        vm.PropertyChanged += (_, args) =>
        {

            if (args.PropertyName == nameof(ConfigurationViewModel.ValidationErrorsByPointer))
            {

                republished++;

            }

        };

        field.StringValue = "not-a-session-id";

        Assert.True(vm.HasFieldErrors);

        Assert.True(vm.ValidationErrorsByPointer.ContainsKey("retention.protectedSessionIds"));

        Assert.Equal(1, republished);

        republished = 0;

        field.StringValue = "11111111-1111-1111-1111-111111111111";

        Assert.False(vm.HasFieldErrors);

        Assert.Empty(vm.ValidationErrorsByPointer);

        Assert.Equal(1, republished);

    }

    private static async Task<ConfigurationViewModel> CreateLoadedViewModelAsync()
    {

        ConfigurationViewModel vm = new(
            new FakeConfigurationStore(static () => new ArcanumSettings()),
            new NoopDialogService(),
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance,
            ImmediateArcanumClientMutationBoundary.Instance);

        for (int attempt = 0; attempt < 100; attempt++)
        {

            if (vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal))
            {

                return vm;

            }

            await Task.Delay(20);

        }

        Assert.Fail("Timed out waiting for the configuration to load.");

        return vm;

    }

    private static void OpenEverySection(ConfigurationViewModel vm)
    {

        foreach (ConfigSection section in Enum.GetValues<ConfigSection>())
        {

            _ = vm.GetOrCreateGenericSection(section);

        }

    }

    private static GenericSettingFieldViewModel Field(
        ConfigurationViewModel vm,
        ConfigSection section,
        string key) =>
        Assert.Single(
            vm.GetOrCreateGenericSection(section).Fields,
            field => field.Descriptor.Key == key);

    private sealed class FakeConfigurationStore(Func<ArcanumSettings> read) : IArcanumConfigurationStore
    {

        public string ConfigurationFilePath { get; } =
            Path.Combine(Path.GetTempPath(), "compendium-notification-never-written", "arcanum.json");

        public event EventHandler? ExternalChange;

        public void RaiseExternalChange() => ExternalChange?.Invoke(this, EventArgs.Empty);

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) => Task.FromResult(read());

        public Task<ConfigurationWriteResult> WriteAsync(ArcanumSettings settings, CancellationToken ct = default)
            => Task.FromResult(new ConfigurationWriteResult(true, [], null));

        public void Dispose()
        {
        }

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
            => Task.FromResult(true);

    }

}
