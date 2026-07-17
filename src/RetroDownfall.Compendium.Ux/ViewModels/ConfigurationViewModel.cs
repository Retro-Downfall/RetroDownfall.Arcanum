using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Compendium.Ux.Models;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class ConfigurationViewModel : ObservableObject
{

    private readonly IArcanumConfigurationStore _store;

    private readonly IDialogService _dialogService;

    private readonly IUiDispatcher _uiDispatcher;

    private readonly Dictionary<ConfigSection, GenericSectionViewModel> _genericSections = new();

    private readonly HashSet<INotifyPropertyChanged> _nestedDirtySubscriptions = [];

    [ObservableProperty] private bool _isDirty;

    [ObservableProperty] private bool _isSaving;

    [ObservableProperty] private string _statusMessage = string.Empty;

    [ObservableProperty] private DateTimeOffset? _lastSavedAt;

    [ObservableProperty] private ConfigSection _selectedSection = ConfigSection.Host;

    [ObservableProperty] private bool _hasExternalChange;

    [ObservableProperty] private IReadOnlyDictionary<string, string> _validationErrorsByPointer = new Dictionary<string, string>();

    private ArcanumSettings _snapshot = new();

    public HostSectionViewModel Host { get; } = new();

    public ProvidersSectionViewModel Providers { get; } = new();

    public IntelligenceSectionViewModel Intelligence { get; } = new();

    public McpSectionViewModel Mcp { get; } = new();

    public OrchestrationSectionViewModel Orchestration { get; } = new();

    public SecuritySectionViewModel Security { get; } = new();

    public StorageSectionViewModel Storage { get; } = new();

    public TheForgeSectionViewModel Forge { get; } = new();

    public ProvingGroundsSectionViewModel ProvingGrounds { get; } = new();

    public CliSectionViewModel Cli { get; } = new();

    public ServerSectionViewModel Server { get; } = new();

    public CommLinkSectionViewModel CommLink { get; } = new();

    public ScryingSectionViewModel Scrying { get; } = new();

    public IReadOnlyList<AiProviderKind> ProviderKinds { get; } = Enum.GetValues<AiProviderKind>();

    public IReadOnlyList<ArcanumTheme> CliThemes { get; } = Enum.GetValues<ArcanumTheme>();

    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    public ObservableCollection<SectionDescriptor> Sections { get; } = new(SectionDescriptors.All);

    public IAsyncRelayCommand SaveCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand CancelCommand { get; }

    public IRelayCommand<SectionDescriptor> SelectSectionCommand { get; }

    public ConfigurationViewModel(
        IArcanumConfigurationStore store,
        IDialogService dialogService,
        IUiDispatcher uiDispatcher,
        LocalCertificateGenerator? certificateGenerator = null)
    {

        _store = store;

        _dialogService = dialogService;

        _uiDispatcher = uiDispatcher;

        Host.AttachServices(certificateGenerator ?? new LocalCertificateGenerator(), dialogService);

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsDirty && !IsSaving && !HasExternalChange);

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

    }

    public void MarkDirty()
    {

        IsDirty = true;

        SaveCommand.NotifyCanExecuteChanged();

        CancelCommand.NotifyCanExecuteChanged();

    }

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

    public GenericSectionViewModel GetOrCreateGenericSection(ConfigSection section)
    {

        if (_genericSections.TryGetValue(section, out GenericSectionViewModel? existing))
        {

            return existing;

        }

        GenericSectionViewModel created = new(this, section);

        ReloadGenericSection(created);

        _genericSections[section] = created;

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

            string path = _store.ConfigurationFilePath;

            string message = $"Could not load {path}:{Environment.NewLine}{Environment.NewLine}{ex.Message}";

            await _uiDispatcher.InvokeAsync(() =>
            {

                StatusMessage = $"Failed to load {path}";

            }).ConfigureAwait(false);

            await _dialogService.ShowAlertAsync("Corrupt arcanum.json", message).ConfigureAwait(false);

        }

    }

    private void ApplyLoadedSettings(ArcanumSettings settings)
    {

        _snapshot = settings;

        Host.LoadFrom(settings.Host);

        Providers.LoadFrom(
            settings.Providers,
            settings.DefaultModel,
            settings.FastModel);

        Intelligence.LoadFrom(settings.Intelligence);

        Mcp.LoadFrom(settings.Mcp);

        Orchestration.LoadFrom(settings.Daemon, settings.Apprentices, settings.Conclave);

        Security.LoadFrom(settings.Security, settings.Ward);

        Storage.LoadFrom(
            settings.Grimoire,
            settings.Sessions,
            settings.EventBus,
            settings.Logs,
            settings.Workspaces);

        Forge.LoadFrom(
            settings.Spells,
            settings.Campaigns,
            settings.Perception,
            settings.Prompts,
            settings.Codex);

        ProvingGrounds.LoadFrom(settings.ProvingGrounds);

        Cli.LoadFrom(settings.Cli);

        Server.LoadFrom(settings.Server);

        CommLink.LoadFrom(settings.CommLink);

        Scrying.LoadFrom(settings.Scrying);

        foreach (GenericSectionViewModel generic in _genericSections.Values)
        {

            ReloadGenericSection(generic);

        }

        IsDirty = false;

        HasExternalChange = false;

        ValidationErrorsByPointer = new Dictionary<string, string>();

        LastSavedAt = _store.GetLastWriteTimeUtc();

        StatusMessage = $"Loaded from {_store.ConfigurationFilePath}";

    }

    private void ReloadGenericSection(GenericSectionViewModel section)
    {

        string? prefix = SectionDescriptors.KeyPrefix(section.Section);

        IEnumerable<SettingDescriptor> descriptors = SettingDescriptors.All
            .Where(d =>
                prefix is not null
                    ? d.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    : d.Section == section.Section);

        List<GenericSettingFieldViewModel> fields = [];

        foreach (SettingDescriptor descriptor in descriptors)
        {

            // Skip dictionary kinds in the generic editor for v1 (complex nested maps).
            if (descriptor.Kind == SettingKind.Dictionary)
            {

                continue;

            }

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

                    StatusMessage = "Saved arcanum.json";

                    ValidationErrorsByPointer = new Dictionary<string, string>();

                }
                else
                {

                    StatusMessage = result.ErrorMessage ?? "Save failed.";

                    Dictionary<string, string> byPointer = new(StringComparer.Ordinal);

                    foreach (ConfigurationValidationError error in result.ValidationErrors)
                    {

                        byPointer[error.Pointer] = error.Detail;

                    }

                    ValidationErrorsByPointer = byPointer;

                }

            }).ConfigureAwait(false);

            if (!result.IsSuccess && result.ValidationErrors.Count > 0)
            {

                string first = result.ValidationErrors[0].Detail;

                await _dialogService.ShowAlertAsync("Validation Error", first).ConfigureAwait(false);

            }

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

    }

    private Task CancelAsync()
    {

        if (IsSaving || !IsDirty)
        {

            return Task.CompletedTask;

        }

        ApplyLoadedSettings(_snapshot);

        StatusMessage = "Discarded local edits.";

        return Task.CompletedTask;

    }

    public ArcanumSettings BuildSettings()
    {

        ArcanumSettings polished = _snapshot with
        {

            Host = Host.Build(),

            Providers = Providers.BuildProviders(),

            DefaultModel = string.IsNullOrWhiteSpace(Providers.DefaultModel) ? null : Providers.DefaultModel,

            FastModel = string.IsNullOrWhiteSpace(Providers.FastModel) ? null : Providers.FastModel,

            Conclave = Orchestration.BuildConclave(),

            Intelligence = Intelligence.Build(),

            Mcp = Mcp.Build(),

            Daemon = Orchestration.BuildDaemon(),

            Apprentices = Orchestration.BuildApprentices(),

            Ward = Security.BuildWard(),

            Security = Security.BuildSecurity(),

            Grimoire = Storage.BuildGrimoire(),

            Sessions = Storage.BuildSessions(),

            EventBus = Storage.BuildEventBus(),

            Logs = Storage.BuildLogs(),

            Workspaces = Storage.BuildWorkspaces(),

            Spells = Forge.BuildSpells(),

            Campaigns = Forge.BuildCampaigns(),

            Perception = Forge.BuildPerception(),

            Prompts = Forge.BuildPrompts(),

            Codex = Forge.BuildCodex(),

            ProvingGrounds = ProvingGrounds.Build(),

            Cli = Cli.Build(),

            Server = Server.Build(),

            CommLink = CommLink.Build(),

            Scrying = Scrying.Build(),

        };

        List<GenericSettingFieldViewModel> genericFields = _genericSections.Values
            .SelectMany(s => s.Fields)
            .ToList();

        return GenericSettingsUpdater.ApplyFields(polished, genericFields);

    }

    private void WireDirtyTracking()
    {

        foreach (ObservableObject section in AllSectionViewModels())
        {

            section.PropertyChanged += (_, _) => MarkDirty();

        }

        Providers.Providers.CollectionChanged += OnProvidersCollectionChanged;

        Orchestration.Jobs.CollectionChanged += OnJobsCollectionChanged;

        foreach (ProvidersSectionViewModel.ProviderViewModel provider in Providers.Providers)
        {

            SubscribeProviderDirty(provider);

        }

        foreach (OrchestrationSectionViewModel.UnseenServantJobViewModel job in Orchestration.Jobs)
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

                if (nested is OrchestrationSectionViewModel.UnseenServantJobViewModel)
                {

                    UnsubscribeNestedDirty(nested);

                }

            }

            foreach (OrchestrationSectionViewModel.UnseenServantJobViewModel job in Orchestration.Jobs)
            {

                SubscribeNestedDirty(job);

            }

        }

    }

    private void SubscribeProviderDirty(ProvidersSectionViewModel.ProviderViewModel provider)
    {

        SubscribeNestedDirty(provider);

        provider.Models.CollectionChanged += OnProviderModelsCollectionChanged;

        foreach (ProvidersSectionViewModel.ModelEntryViewModel model in provider.Models)
        {

            SubscribeNestedDirty(model);

        }

    }

    private void UnsubscribeProviderDirty(ProvidersSectionViewModel.ProviderViewModel provider)
    {

        provider.Models.CollectionChanged -= OnProviderModelsCollectionChanged;

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

        yield return Intelligence;

        yield return Mcp;

        yield return Orchestration;

        yield return Security;

        yield return Storage;

        yield return Forge;

        yield return ProvingGrounds;

        yield return Cli;

        yield return Server;

        yield return CommLink;

        yield return Scrying;

    }

}
