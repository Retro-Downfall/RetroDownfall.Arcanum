using Avalonia.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using RetroDownfall.Compendium.Ux.Views.Controls;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

/// <summary>
/// The editor must fail visibly rather than silently: a configuration that never loaded may not be
/// saved over, an invalid field may not be dropped while Save reports success, and a save that failed
/// for a non-validation reason must reach the operator.
/// </summary>
[Collection("AvaloniaBinding")]
public sealed class FailClosedEditorTests
{

    [Fact]
    public async Task Failed_load_blocks_saving_over_the_unread_configuration()
    {

        FakeConfigurationStore store = new(
            static () => throw new InvalidOperationException(
                "Failed to parse arcanum.json: security.futureKnob: Unknown or obsolete configuration path"));

        RecordingDialogService dialogs = new();

        ConfigurationViewModel vm = CreateViewModel(store, dialogs);

        await WaitForAsync(() => vm.LoadFailed);

        Assert.True(vm.LoadFailed);

        vm.MarkDirty();

        Assert.True(vm.IsDirty);

        Assert.False(vm.SaveCommand.CanExecute(null));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(store.Writes);

        Assert.Contains(dialogs.Alerts, alert => alert.Title == "Cannot save");

    }

    [Fact]
    public async Task Successful_load_after_a_failure_clears_the_block()
    {

        bool fail = true;

        FakeConfigurationStore store = new(
            () => fail
                ? throw new InvalidOperationException("Failed to parse arcanum.json")
                : new ArcanumSettings { Host = new HostSettings { Port = 5055 } });

        ConfigurationViewModel vm = CreateViewModel(store, new RecordingDialogService());

        await WaitForAsync(() => vm.LoadFailed);

        fail = false;

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.False(vm.LoadFailed);

        Assert.Equal(5055, vm.Host.Port);

        vm.MarkDirty();

        Assert.True(vm.SaveCommand.CanExecute(null));

    }

    [Fact]
    public async Task Invalid_generic_field_blocks_save_and_is_reported_against_its_own_key()
    {

        FakeConfigurationStore store = new(
            static () => new ArcanumSettings { Host = new HostSettings { Port = 5001 } });

        RecordingDialogService dialogs = new();

        ConfigurationViewModel vm = CreateViewModel(store, dialogs);

        await WaitForAsync(() => !vm.LoadFailed && vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal));

        GenericSectionViewModel section = vm.GetOrCreateGenericSection(ConfigSection.Retention);

        GenericSettingFieldViewModel field = Assert.Single(
            section.Fields,
            f => f.Descriptor.Key == "retention.protectedSessionIds");

        field.StringValue = "not-a-session-id";

        Assert.True(vm.IsDirty);

        Assert.True(vm.HasFieldErrors);

        Assert.False(vm.SaveCommand.CanExecute(null));

        Assert.True(vm.ValidationErrorsByPointer.ContainsKey("retention.protectedSessionIds"));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(store.Writes);

        Assert.Contains(dialogs.Alerts, alert => alert.Title == "Invalid settings");

        Assert.Contains(
            dialogs.Alerts,
            alert => alert.Message.Contains("retention.protectedSessionIds", StringComparison.Ordinal));

        Assert.NotEqual("Saved arcanum.json", vm.StatusMessage);

        field.StringValue = "11111111-1111-1111-1111-111111111111";

        Assert.False(vm.HasFieldErrors);

        Assert.True(vm.SaveCommand.CanExecute(null));

    }

    [Fact]
    public async Task Save_failure_without_validation_errors_still_reaches_the_operator()
    {

        FakeConfigurationStore store = new(
            static () => new ArcanumSettings { Host = new HostSettings { Port = 5001 } })
        {
            NextWriteResult = new ConfigurationWriteResult(
                false,
                [],
                "arcanum.json changed on disk after it was loaded. Reload the configuration, review the newer values, and save again."),
        };

        RecordingDialogService dialogs = new();

        ConfigurationViewModel vm = CreateViewModel(store, dialogs);

        await WaitForAsync(() => vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal));

        vm.Host.Port = 5100;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains(dialogs.Alerts, alert => alert.Title == "Save failed");

        Assert.Contains(
            dialogs.Alerts,
            alert => alert.Message.Contains("changed on disk", StringComparison.Ordinal));

        Assert.NotNull(vm.LastErrorMessage);

        Assert.Contains("changed on disk", vm.LastErrorMessage);

        Assert.True(vm.IsDirty);

    }

    [Fact]
    public async Task Cancel_keeps_the_external_change_warning_because_it_does_not_re_read_disk()
    {

        FakeConfigurationStore store = new(
            static () => new ArcanumSettings { Host = new HostSettings { Port = 5001 } });

        ConfigurationViewModel vm = CreateViewModel(store, new RecordingDialogService());

        await WaitForAsync(() => vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal));

        vm.Host.Port = 5100;

        store.RaiseExternalChange();

        Assert.True(vm.HasExternalChange);

        Assert.True(vm.IsDirty);

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirty);

        Assert.Equal(5001, vm.Host.Port);

        Assert.True(vm.HasExternalChange);

        Assert.False(vm.SaveCommand.CanExecute(null));

    }

    [Fact]
    public async Task Closing_with_unsaved_edits_requires_confirmation()
    {

        FakeConfigurationStore store = new(
            static () => new ArcanumSettings { Host = new HostSettings { Port = 5001 } });

        RecordingDialogService dialogs = new() { ConfirmResult = false };

        ConfigurationViewModel vm = CreateViewModel(store, dialogs);

        await WaitForAsync(() => vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal));

        Assert.True(await vm.ConfirmDiscardOnExitAsync());

        Assert.Empty(dialogs.Confirms);

        vm.Host.Port = 5100;

        Assert.False(await vm.ConfirmDiscardOnExitAsync());

        Assert.Contains(dialogs.Confirms, confirm => confirm.Title == "Unsaved changes");

        dialogs.ConfirmResult = true;

        Assert.True(await vm.ConfirmDiscardOnExitAsync());

    }

    [Fact]
    public async Task A_default_configuration_produces_no_field_errors_in_any_section()
    {

        FakeConfigurationStore store = new(static () => new ArcanumSettings());

        ConfigurationViewModel vm = CreateViewModel(store, new RecordingDialogService());

        await WaitForAsync(() => vm.StatusMessage.StartsWith("Loaded", StringComparison.Ordinal));

        List<string> offenders = [];

        foreach (ConfigSection section in Enum.GetValues<ConfigSection>())
        {

            GenericSectionViewModel generic = vm.GetOrCreateGenericSection(section);

            offenders.AddRange(generic.Fields
                .Where(static field => field.HasError)
                .Select(static field => $"{field.Descriptor.Key}: {field.ErrorMessage}"));

        }

        Assert.True(
            offenders.Count == 0,
            $"Default settings must not block Save:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");

        Assert.False(vm.HasFieldErrors);

    }

    [Fact]
    public void Malformed_dictionary_json_is_reported_on_the_field_that_owns_it()
    {

        SettingDescriptor descriptor = Assert.IsType<SettingDescriptor>(
            SettingDescriptors.Find("cost.pricing.modelPricing"));

        Assert.Equal(SettingKind.Dictionary, descriptor.Kind);

        Assert.NotNull(GenericSettingsUpdater.ResolveValueType(descriptor.Key));

        GenericSettingFieldViewModel field = new(descriptor, null);

        Assert.False(field.HasError);

        field.StringValue = "{\"gpt-4o\": {\"inputPer1M\": 2.5,}}";

        Assert.True(field.HasError);

        Assert.Contains("JSON", field.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        field.StringValue = "{\"gpt-4o\": {\"inputPer1M\": 2.5}}";

        Assert.False(field.HasError);

    }

    [Fact]
    public async Task Certificate_generation_failure_is_reported_and_leaves_https_untouched()
    {

        HostSectionViewModel host = new();

        RecordingDialogService dialogs = new();

        host.AttachServices(
            static () => throw new IOException("No space left on device"),
            dialogs);

        host.LoadFrom(new HostSettings());

        await host.GenerateLocalCertificateCommand.ExecuteAsync(null);

        Assert.False(host.HttpsEnabled);

        Assert.Equal(string.Empty, host.HttpsCertificatePath);

        Assert.Equal(string.Empty, host.HttpsPrivateKeyPath);

        Assert.Contains(dialogs.Alerts, alert => alert.Title == "Could not generate certificate");

        Assert.Contains(
            dialogs.Alerts,
            alert => alert.Message.Contains("No space left on device", StringComparison.Ordinal));

    }

    [Fact]
    public void SaveBar_shows_the_save_error_and_keeps_Reload_reachable_when_the_file_is_unreadable()
    {

        SaveBar bar = new()
        {
            IsDirty = true,
            LastErrorMessage = "arcanum.json changed on disk after it was loaded.",
        };

        TextBlock status = Assert.IsType<TextBlock>(bar.FindControl<TextBlock>("StatusLabel"));

        Button reload = Assert.IsType<Button>(bar.FindControl<Button>("ReloadButton"));

        Assert.Equal("arcanum.json changed on disk after it was loaded.", status.Text);

        Assert.False(reload.IsVisible);

        bar.LoadFailed = true;

        Assert.Contains("Could not read arcanum.json", status.Text);

        Assert.True(reload.IsVisible);

        bar.LoadFailed = false;

        bar.LastErrorMessage = null;

        Assert.Equal("Unsaved changes", status.Text);

    }

    private static ConfigurationViewModel CreateViewModel(
        IArcanumConfigurationStore store,
        IDialogService dialogService) =>
        new(
            store,
            dialogService,
            new SynchronousUiDispatcher(),
            NullLogger<ConfigurationViewModel>.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {

        for (int attempt = 0; attempt < 100; attempt++)
        {

            if (condition())
            {

                return;

            }

            await Task.Delay(20);

        }

        Assert.Fail("Timed out waiting for the view model to settle.");

    }

    private sealed class FakeConfigurationStore(Func<ArcanumSettings> read) : IArcanumConfigurationStore
    {

        public string ConfigurationFilePath { get; } =
            Path.Combine(Path.GetTempPath(), "compendium-fake-never-written", "arcanum.json");

        public ConfigurationWriteResult NextWriteResult { get; set; } = new(true, [], null);

        public List<ArcanumSettings> Writes { get; } = [];

        public event EventHandler? ExternalChange;

        public void RaiseExternalChange() => ExternalChange?.Invoke(this, EventArgs.Empty);

        public DateTimeOffset? GetLastWriteTimeUtc() => null;

        public Task<ArcanumSettings> ReadAsync(CancellationToken ct = default) => Task.FromResult(read());

        public Task<ConfigurationWriteResult> WriteAsync(ArcanumSettings settings, CancellationToken ct = default)
        {

            if (NextWriteResult.IsSuccess)
            {

                Writes.Add(settings);

            }

            return Task.FromResult(NextWriteResult);

        }

        public void Dispose()
        {
        }

    }

    private sealed class RecordingDialogService : IDialogService
    {

        public List<(string Title, string Message)> Alerts { get; } = [];

        public List<(string Title, string Message)> Confirms { get; } = [];

        public bool ConfirmResult { get; set; } = true;

        public Task ShowAlertAsync(string title, string message, string cancel = "OK")
        {

            Alerts.Add((title, message));

            return Task.CompletedTask;

        }

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
        {

            Confirms.Add((title, message));

            return Task.FromResult(ConfirmResult);

        }

    }

}
