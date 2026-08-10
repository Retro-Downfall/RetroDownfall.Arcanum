using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ConfigurationViewModel : ObservableObject
{

    private readonly IArcanumConfigurationStore _store;

    private readonly IDialogService _dialogService;

    private readonly IUiDispatcher _uiDispatcher;

    private readonly ILogger<ConfigurationViewModel> _logger;

    private readonly Dictionary<ConfigSection, GenericSectionViewModel> _genericSections = new();

    private readonly HashSet<INotifyPropertyChanged> _nestedDirtySubscriptions = [];

    private readonly HashSet<ProvidersSectionViewModel.ProviderViewModel> _modelsSubscribedProviders = [];

    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private bool _isSaving;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private DateTimeOffset? _lastSavedAt;

    [ObservableProperty] private ConfigSection _selectedSection = ConfigSection.Edition;

    [ObservableProperty] private bool _hasExternalChange;

    [ObservableProperty] private IReadOnlyDictionary<string, string> _validationErrorsByPointer = new Dictionary<string, string>();

    /// <summary>
    /// Set when the initial (or a subsequent) read of arcanum.json failed. The editor is then bound to
    /// fabricated defaults rather than the operator's file, so saving is blocked until a read succeeds.
    /// </summary>
    [ObservableProperty] private bool _loadFailed;

    [ObservableProperty] private bool _hasFieldErrors;

    [ObservableProperty] private string? _lastErrorMessage;

    private ArcanumSettings _snapshot = new();

    private Dictionary<string, string> _serverValidationErrors = new(StringComparer.Ordinal);

    public HostSectionViewModel Host { get; } = new();

    public ProvidersSectionViewModel Providers { get; }

    public DaemonSectionViewModel Daemon { get; }

    public CliSectionViewModel Cli { get; } = new();

    public PresetsSectionViewModel Presets { get; }

    public IReadOnlyList<AiProviderKind> ProviderKinds { get; } = Enum.GetValues<AiProviderKind>();

    public IReadOnlyList<ArcanumTheme> CliThemes { get; } = Enum.GetValues<ArcanumTheme>();

    public IReadOnlyList<RetroDownfall.Arcanum.Core.Logging.LogLevel> LogLevels { get; } = Enum.GetValues<RetroDownfall.Arcanum.Core.Logging.LogLevel>();

    public ObservableCollection<SectionDescriptor> Sections { get; } = new(SectionDescriptors.All);

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand CancelCommand { get; }

    public IRelayCommand<SectionDescriptor> SelectSectionCommand { get; }

    public ConfigurationViewModel(
        IArcanumConfigurationStore store,
        IDialogService dialogService,
        IUiDispatcher uiDispatcher,
        ILogger<ConfigurationViewModel> logger,
        LocalCertificateGenerator? certificateGenerator = null,
        IConfigurationPresetService? presetService = null,
        IFamiliarProbeClient? probeClient = null)
    {

        _store = store;

        _dialogService = dialogService;

        _uiDispatcher = uiDispatcher;

        _logger = logger;

        // Initialize view models with dialog service for confirmation dialogs
        Providers = new ProvidersSectionViewModel(dialogService, probeClient);
        Daemon = new DaemonSectionViewModel(dialogService);
        Host.AttachServices(certificateGenerator ?? new LocalCertificateGenerator(), dialogService);

        Presets = new PresetsSectionViewModel(this, presetService, uiDispatcher);

        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            () => IsDirty && !IsSaving && !HasExternalChange && !LoadFailed && !HasFieldErrors);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsSaving);

        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsDirty && !IsSaving);

        SelectSectionCommand = new RelayCommand<SectionDescriptor>(OnSelectSection);

        _store.ExternalChange += OnExternalChange;

        WireDirtyTracking();

        // Observe the startup load so parse/IO failures surface as alerts instead of unobserved tasks.
        _ = ObserveLoadAsync();

    }

    private async Task ObserveLoadAsync()
    {

        await LoadAsync().ConfigureAwait(false);

        await Presets.RefreshAsync().ConfigureAwait(false);

    }

    public void MarkDirty()
    {

        IsDirty = true;

        RefreshFieldValidation();

        SaveCommand.NotifyCanExecuteChanged();

        CancelCommand.NotifyCanExecuteChanged();

    }

    /// <summary>
    /// Republishes the pointer-keyed validation surface as the union of the descriptor field errors
    /// computed in the editor and the errors returned by the last write attempt, so a rejected value is
    /// rendered next to the control that owns it instead of being dropped on Save.
    /// </summary>
    private void RefreshFieldValidation()
    {

        Dictionary<string, string> merged = new(StringComparer.Ordinal);

        bool anyFieldError = false;

        foreach (GenericSettingFieldViewModel field in AllGenericFields())
        {

            if (!field.HasError)
            {

                continue;

            }

            anyFieldError = true;

            merged[field.Descriptor.Key] = field.ErrorMessage ?? "This value is not valid.";

        }

        foreach (KeyValuePair<string, string> serverError in _serverValidationErrors)
        {

            merged[serverError.Key] = serverError.Value;

        }

        HasFieldErrors = anyFieldError;

        ValidationErrorsByPointer = merged;

    }

    private IEnumerable<GenericSettingFieldViewModel> AllGenericFields() =>
        _genericSections.Values
            .ToArray()
            .SelectMany(static section => section.Fields);

    private List<string> InvalidFieldSummaries() =>
        AllGenericFields()
            .Where(static field => field.HasError)
            .Select(static field => $"{field.Descriptor.Key}: {field.ErrorMessage}")
            .ToList();

    partial void OnIsDirtyChanged(bool value)
    {

        SaveCommand.NotifyCanExecuteChanged();

        CancelCommand.NotifyCanExecuteChanged();

    }

    partial void OnIsSavingChanged(bool value)
    {

        SaveCommand.NotifyCanExecuteChanged();

        RefreshCommand.NotifyCanExecuteChanged();

        CancelCommand.NotifyCanExecuteChanged();

    }

    partial void OnHasExternalChangeChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnLoadFailedChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnHasFieldErrorsChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    public GenericSectionViewModel GetOrCreateGenericSection(ConfigSection section)
    {

        if (_genericSections.TryGetValue(section, out GenericSectionViewModel? existing))
        {

            return existing;

        }

        GenericSectionViewModel created = new(this, section);

        ReloadGenericSection(created);

        _genericSections[section] = created;

        RefreshFieldValidation();

        return created;

    }

    private void OnSelectSection(SectionDescriptor? descriptor)
    {

        if (descriptor is not null)
        {

            SelectedSection = descriptor.Section;

        }

    }

    private void OnExternalChange(object? sender, EventArgs e)
    {

        _uiDispatcher.Post(() =>
        {

            HasExternalChange = true;

            StatusMessage = "arcanum.json changed on disk.";

        });

    }

    private async Task LoadAsync()
    {

        try
        {

            ArcanumSettings settings = await _store.ReadAsync(CancellationToken.None).ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() => ApplyLoadedSettings(settings)).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            _logger.LogError(ex, "Failed to load configuration from {Path}", _store.ConfigurationFilePath);

            string path = _store.ConfigurationFilePath;

            string message = $"Could not load {path}:{Environment.NewLine}{Environment.NewLine}{ex.Message}";

            await _uiDispatcher.InvokeAsync(() =>
            {

                // Fail closed: the editor is showing fabricated defaults, so a Save here would replace
                // the operator's real file with them. Saving stays blocked until a read succeeds.
                LoadFailed = true;

                LastErrorMessage = ex.Message;

                StatusMessage = $"Failed to load {path}";

            }).ConfigureAwait(false);

            await _dialogService.ShowAlertAsync("Corrupt arcanum.json", message).ConfigureAwait(false);

        }

    }

    private void ApplyLoadedSettings(ArcanumSettings settings)
    {

        RestoreSections(settings);

        LoadFailed = false;

        HasExternalChange = false;

        LastErrorMessage = null;

        LastSavedAt = _store.GetLastWriteTimeUtc();

        StatusMessage = $"Loaded from {_store.ConfigurationFilePath}";

    }

    /// <summary>
    /// Rebinds every section to <paramref name="settings"/> and clears the dirty/validation state.
    /// Deliberately leaves <see cref="HasExternalChange"/> and <see cref="LastSavedAt"/> alone so a
    /// snapshot restore (Cancel) never dismisses the watcher's on-disk-change warning without a reload.
    /// </summary>
    private void RestoreSections(ArcanumSettings settings)
    {

        _snapshot = settings;

        Host.LoadFrom(settings.Host);

        Providers.LoadFrom(
            settings.Providers,
            settings.DefaultModel,
            settings.FastModel);

        Daemon.LoadFrom(settings.Daemon);

        Cli.LoadFrom(settings.Cli);

        foreach (GenericSectionViewModel generic in _genericSections.Values.ToArray())
        {

            ReloadGenericSection(generic);

        }

        IsDirty = false;

        _serverValidationErrors = new Dictionary<string, string>(StringComparer.Ordinal);

        RefreshFieldValidation();

    }

    private void ReloadGenericSection(GenericSectionViewModel section)
    {

        IEnumerable<SettingDescriptor> descriptors = SettingDescriptors.All
            .Where(descriptor => descriptor.Section == section.Section);

        List<GenericSettingFieldViewModel> fields = [];

        foreach (SettingDescriptor descriptor in descriptors)
        {

            object? value = GenericSettingsUpdater.ReadValue(_snapshot, descriptor.Key);

            fields.Add(new GenericSettingFieldViewModel(descriptor, value));

        }

        section.LoadFrom(fields);

    }

    private async Task SaveAsync()
    {

        if (IsSaving)
        {

            return;

        }

        if (LoadFailed)
        {

            string path = _store.ConfigurationFilePath;

            await _uiDispatcher.InvokeAsync(() =>
            {

                LastErrorMessage = $"Cannot save: {path} was never read successfully.";

                StatusMessage = $"Cannot save {path}";

            }).ConfigureAwait(false);

            await _dialogService
                .ShowAlertAsync(
                    "Cannot save",
                    $"Compendium never read {path} successfully, so this window shows default settings rather than yours."
                    + $"{Environment.NewLine}{Environment.NewLine}Repair the file on disk and choose Reload before saving.")
                .ConfigureAwait(false);

            return;

        }

        List<string> invalidFields = InvalidFieldSummaries();

        if (invalidFields.Count > 0)
        {

            await _uiDispatcher.InvokeAsync(() =>
            {

                LastErrorMessage = $"{invalidFields.Count} setting(s) must be corrected before saving.";

                StatusMessage = "Fix the highlighted settings before saving.";

            }).ConfigureAwait(false);

            await _dialogService
                .ShowAlertAsync(
                    "Invalid settings",
                    $"These settings cannot be saved until they are corrected:{Environment.NewLine}{Environment.NewLine}"
                    + string.Join(Environment.NewLine, invalidFields))
                .ConfigureAwait(false);

            return;

        }

        IsSaving = true;

        try
        {

            ArcanumSettings settings = BuildSettings();

            ConfigurationWriteResult result = await _store.WriteAsync(settings, CancellationToken.None).ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {

                if (result.IsSuccess)
                {

                    _snapshot = settings;

                    IsDirty = false;

                    HasExternalChange = false;

                    LastSavedAt = _store.GetLastWriteTimeUtc();

                    LastErrorMessage = null;

                    StatusMessage = "Saved arcanum.json";

                    _serverValidationErrors = new Dictionary<string, string>(StringComparer.Ordinal);

                    RefreshFieldValidation();

                }
                else
                {

                    LastErrorMessage = result.ErrorMessage ?? "Save failed.";

                    StatusMessage = result.ErrorMessage ?? "Save failed.";

                    Dictionary<string, string> byPointer = new(StringComparer.Ordinal);

                    foreach (ConfigurationValidationError error in result.ValidationErrors)
                    {

                        byPointer[error.Pointer] = error.Detail;

                    }

                    _serverValidationErrors = byPointer;

                    RefreshFieldValidation();

                }

            }).ConfigureAwait(false);

            if (!result.IsSuccess)
            {

                // Every unsuccessful save is surfaced, not only the validation ones: locked files,
                // write-lock timeouts and stale-fingerprint refusals carry no validation errors and
                // would otherwise be invisible behind the SaveBar's "Unsaved changes" text.
                bool hasValidationErrors = result.ValidationErrors.Count > 0;

                string detail = hasValidationErrors
                    ? result.ValidationErrors[0].Detail
                    : result.ErrorMessage ?? "Save failed.";

                await _dialogService
                    .ShowAlertAsync(hasValidationErrors ? "Validation Error" : "Save failed", detail)
                    .ConfigureAwait(false);

            }

        }
        catch (Exception ex)
        {

            await _uiDispatcher.InvokeAsync(() =>
            {

                LastErrorMessage = ex.Message;

                StatusMessage = "Could not save arcanum.json";

            }).ConfigureAwait(false);

            await _dialogService
                .ShowAlertAsync(
                    "Save failed",
                    $"Compendium could not build or save the configuration:{Environment.NewLine}{Environment.NewLine}{ex.Message}")
                .ConfigureAwait(false);

        }
        finally
        {

            await _uiDispatcher.InvokeAsync(() => IsSaving = false).ConfigureAwait(false);

        }

    }

    private async Task RefreshAsync()
    {

        if (IsSaving)
        {

            return;

        }

        if (IsDirty)
        {

            bool discard = await _dialogService
                .ShowConfirmAsync("Reload", "Discard local edits and reload from disk?")
                .ConfigureAwait(false);

            if (!discard)
            {

                return;

            }

        }

        await LoadAsync().ConfigureAwait(false);

        await Presets.RefreshAsync().ConfigureAwait(false);

    }

    internal Task ReloadAfterPresetMutationAsync() => LoadAsync();

    private Task CancelAsync()
    {

        if (IsSaving || !IsDirty)
        {

            return Task.CompletedTask;

        }

        RestoreSections(_snapshot);

        LastErrorMessage = null;

        StatusMessage = "Discarded local edits.";

        return Task.CompletedTask;

    }

    /// <summary>
    /// Decides whether the window may close. Unsaved edits require an explicit operator confirmation so
    /// closing never silently discards them, matching the Reload-with-edits confirmation.
    /// </summary>
    public async Task<bool> ConfirmDiscardOnExitAsync()
    {

        if (!IsDirty)
        {

            return true;

        }

        return await _dialogService
            .ShowConfirmAsync(
                "Unsaved changes",
                "Discard the unsaved configuration edits and close Compendium?",
                "Discard",
                "Keep editing")
            .ConfigureAwait(false);

    }

    public ArcanumSettings BuildSettings()
    {

        ArcanumSettings polished = _snapshot with
        {

            Host = Host.Build(),

            Providers = Providers.BuildProviders(),

            DefaultModel = string.IsNullOrWhiteSpace(Providers.DefaultModel) ? null : Providers.DefaultModel,

            FastModel = string.IsNullOrWhiteSpace(Providers.FastModel) ? null : Providers.FastModel,

            Daemon = Daemon.Build(),

            Cli = Cli.Build(),

        };

        List<GenericSettingFieldViewModel> genericFields = _genericSections.Values
            .SelectMany(s => s.Fields)
            .ToList();

        return GenericSettingsUpdater.ApplyFields(polished, genericFields, _logger);

    }

    private void WireDirtyTracking()
    {

        foreach (ObservableObject section in AllSectionViewModels())
        {

            section.PropertyChanged += (_, _) => MarkDirty();

        }

        Providers.Providers.CollectionChanged += OnProvidersCollectionChanged;

        Daemon.Jobs.CollectionChanged += OnJobsCollectionChanged;

        foreach (ProvidersSectionViewModel.ProviderViewModel provider in Providers.Providers)
        {

            SubscribeProviderDirty(provider);

        }

        foreach (DaemonSectionViewModel.UnseenServantJobViewModel job in Daemon.Jobs)
        {

            SubscribeNestedDirty(job);

        }

    }

    private void OnProvidersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        MarkDirty();

        if (e.OldItems is not null)
        {

            foreach (object item in e.OldItems)
            {

                if (item is ProvidersSectionViewModel.ProviderViewModel provider)
                {

                    UnsubscribeProviderDirty(provider);

                }

            }

        }

        if (e.NewItems is not null)
        {

            foreach (object item in e.NewItems)
            {

                if (item is ProvidersSectionViewModel.ProviderViewModel provider)
                {

                    SubscribeProviderDirty(provider);

                }

            }

        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {

            foreach (INotifyPropertyChanged nested in _nestedDirtySubscriptions.ToArray())
            {

                if (nested is ProvidersSectionViewModel.ProviderViewModel
                    or ProvidersSectionViewModel.ModelEntryViewModel)
                {

                    UnsubscribeNestedDirty(nested);

                }

            }

            foreach (ProvidersSectionViewModel.ProviderViewModel provider in Providers.Providers)
            {

                SubscribeProviderDirty(provider);

            }

        }

    }

    private void OnJobsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        MarkDirty();

        if (e.OldItems is not null)
        {

            foreach (object item in e.OldItems)
            {

                if (item is INotifyPropertyChanged nested)
                {

                    UnsubscribeNestedDirty(nested);

                }

            }

        }

        if (e.NewItems is not null)
        {

            foreach (object item in e.NewItems)
            {

                if (item is INotifyPropertyChanged nested)
                {

                    SubscribeNestedDirty(nested);

                }

            }

        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {

            foreach (INotifyPropertyChanged nested in _nestedDirtySubscriptions.ToArray())
            {

                if (nested is DaemonSectionViewModel.UnseenServantJobViewModel)
                {

                    UnsubscribeNestedDirty(nested);

                }

            }

            foreach (DaemonSectionViewModel.UnseenServantJobViewModel job in Daemon.Jobs)
            {

                SubscribeNestedDirty(job);

            }

        }

    }

    private void SubscribeProviderDirty(ProvidersSectionViewModel.ProviderViewModel provider)
    {

        SubscribeNestedDirty(provider);

        if (_modelsSubscribedProviders.Add(provider))
        {

            provider.Models.CollectionChanged += OnProviderModelsCollectionChanged;

        }

        foreach (ProvidersSectionViewModel.ModelEntryViewModel model in provider.Models)
        {

            SubscribeNestedDirty(model);

        }

    }

    private void UnsubscribeProviderDirty(ProvidersSectionViewModel.ProviderViewModel provider)
    {

        if (_modelsSubscribedProviders.Remove(provider))
        {

            provider.Models.CollectionChanged -= OnProviderModelsCollectionChanged;

        }

        foreach (ProvidersSectionViewModel.ModelEntryViewModel model in provider.Models)
        {

            UnsubscribeNestedDirty(model);

        }

        UnsubscribeNestedDirty(provider);

    }

    private void OnProviderModelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        MarkDirty();

        if (e.OldItems is not null)
        {

            foreach (object item in e.OldItems)
            {

                if (item is INotifyPropertyChanged nested)
                {

                    UnsubscribeNestedDirty(nested);

                }

            }

        }

        if (e.NewItems is not null)
        {

            foreach (object item in e.NewItems)
            {

                if (item is INotifyPropertyChanged nested)
                {

                    SubscribeNestedDirty(nested);

                }

            }

        }

    }

    private void SubscribeNestedDirty(INotifyPropertyChanged nested)
    {

        if (!_nestedDirtySubscriptions.Add(nested))
        {

            return;

        }

        nested.PropertyChanged += OnNestedPropertyChanged;

    }

    private void UnsubscribeNestedDirty(INotifyPropertyChanged nested)
    {

        if (!_nestedDirtySubscriptions.Remove(nested))
        {

            return;

        }

        nested.PropertyChanged -= OnNestedPropertyChanged;

    }

    private void OnNestedPropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

    private IEnumerable<ObservableObject> AllSectionViewModels()
    {

        yield return Host;

        yield return Providers;

        yield return Daemon;

        yield return Cli;

    }

}
