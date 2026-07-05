using System.Collections.ObjectModel;
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

    public LlamaCppSectionViewModel LlamaCpp { get; } = new();

    public OrchestrationSectionViewModel Orchestration { get; } = new();

    public SecuritySectionViewModel Security { get; } = new();

    public StorageSectionViewModel Storage { get; } = new();

    public ForgeSectionViewModel Forge { get; } = new();

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

    public IRelayCommand<SectionDescriptor> SelectSectionCommand { get; }

    public ConfigurationViewModel(
        IArcanumConfigurationStore store,
        IDialogService dialogService)
    {

        _store = store;

        _dialogService = dialogService;

        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsDirty && !IsSaving && !HasExternalChange);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsSaving);

        SelectSectionCommand = new RelayCommand<SectionDescriptor>(OnSelectSection);

        _store.ExternalChange += OnExternalChange;

        WireDirtyTracking();

        _ = LoadAsync();

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

        HasExternalChange = true;

        StatusMessage = "arcanum.json changed on disk.";

    }

    private async Task LoadAsync()
    {

        ArcanumSettings settings = await _store.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        _snapshot = settings;

        Host.LoadFrom(settings.Host);

        Providers.LoadFrom(
            settings.Providers,
            settings.DefaultModel,
            settings.FastModel);

        Intelligence.LoadFrom(settings.Intelligence);

        Mcp.LoadFrom(settings.Mcp);

        LlamaCpp.LoadFrom(settings.LlamaCpp);

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

        IsDirty = false;

        HasExternalChange = false;

        ValidationErrorsByPointer = new Dictionary<string, string>();

        LastSavedAt = _store.GetLastWriteTimeUtc();

        StatusMessage = $"Loaded from {_store.ConfigurationFilePath}";

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

                if (result.ValidationErrors.Count > 0)

                {

                    string first = result.ValidationErrors[0].Detail;

                    await _dialogService.ShowAlertAsync("Validation Error", first).ConfigureAwait(false);

                }

            }

        }
        finally

        {

            IsSaving = false;

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

    private ArcanumSettings BuildSettings()
    {

        return _snapshot with
        {

            Host = Host.Build(),

            Providers = Providers.BuildProviders(),

            DefaultModel = string.IsNullOrWhiteSpace(Providers.DefaultModel) ? null : Providers.DefaultModel,

            FastModel = string.IsNullOrWhiteSpace(Providers.FastModel) ? null : Providers.FastModel,

            Conclave = Orchestration.BuildConclave(),

            Intelligence = Intelligence.Build(),

            Mcp = Mcp.Build(),

            LlamaCpp = LlamaCpp.Build(),

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

    }

    private void WireDirtyTracking()
    {

        foreach (ObservableObject section in AllSectionViewModels())

        {

            section.PropertyChanged += (_, _) => IsDirty = true;

        }

        Providers.Providers.CollectionChanged += (_, _) => IsDirty = true;

        Orchestration.Jobs.CollectionChanged += (_, _) => IsDirty = true;

    }

    private IEnumerable<ObservableObject> AllSectionViewModels()
    {

        yield return Host;

        yield return Providers;

        yield return Intelligence;

        yield return Mcp;

        yield return LlamaCpp;

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
