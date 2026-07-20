using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Shared New/Open/Edit/Unregister campaign workflows for File/Campaign menus, Atelier roots, and CTAs.
/// </summary>
public interface ICampaignCommandCoordinator
{

    IAsyncRelayCommand NewCampaignCommand { get; }

    IAsyncRelayCommand OpenCampaignCommand { get; }

    IAsyncRelayCommand EditCampaignCommand { get; }

    IAsyncRelayCommand UnregisterCampaignCommand { get; }

    IAsyncRelayCommand NewSpellCommand { get; }

    IAsyncRelayCommand NewPromptCommand { get; }

    bool CanEditOrUnregisterCampaign { get; }

    bool CanCreateCampaignScopedArtifact { get; }

    /// <summary>
    /// Wired by <see cref="Atelier.AtelierViewModel"/> after construction to refresh/focus the tree.
    /// </summary>
    Func<Guid, CancellationToken, Task>? FocusCampaignInAtelierAsync { get; set; }

    event EventHandler? CanEditOrUnregisterChanged;

    Task NewCampaignAsync(CancellationToken cancellationToken = default);

    Task OpenCampaignAsync(CancellationToken cancellationToken = default);

    Task EditActiveCampaignAsync(CancellationToken cancellationToken = default);

    Task UnregisterActiveCampaignAsync(CancellationToken cancellationToken = default);

    Task NewSpellForActiveCampaignAsync(CancellationToken cancellationToken = default);

    Task NewPromptForActiveCampaignAsync(CancellationToken cancellationToken = default);

}

/// <inheritdoc cref="ICampaignCommandCoordinator"/>
public sealed class CampaignCommandCoordinator : ICampaignCommandCoordinator
{

    private readonly ICampaignManagementDataSource _management;

    private readonly IAtelierDataSource _atelierData;

    private readonly ICampaignDialogService _dialogs;

    private readonly IConfirmationDialogService _confirmation;

    private readonly IActiveCampaignService _activeCampaign;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _creationDialogs;

    private readonly INavigationService _navigation;

    private readonly IWhispersService _whispers;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IOptionsMonitor<TheForgeSettings> _settings;

    private readonly IArcanumConnection _connection;

    public CampaignCommandCoordinator(
        ICampaignManagementDataSource management,
        IAtelierDataSource atelierData,
        ICampaignDialogService dialogs,
        IConfirmationDialogService confirmation,
        IActiveCampaignService activeCampaign,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService creationDialogs,
        INavigationService navigation,
        IWhispersService whispers,
        FoundryFloorViewModel foundryFloor,
        IOptionsMonitor<TheForgeSettings> settings,
        IArcanumConnection connection)
    {

        _management = management;

        _atelierData = atelierData;

        _dialogs = dialogs;

        _confirmation = confirmation;

        _activeCampaign = activeCampaign;

        _creationDataSource = creationDataSource;

        _creationDialogs = creationDialogs;

        _navigation = navigation;

        _whispers = whispers;

        _foundryFloor = foundryFloor;

        _settings = settings;

        _connection = connection;

        NewCampaignCommand = new AsyncRelayCommand(
            NewCampaignAsync,
            () => _connection.State is ConnectionState.Connected or ConnectionState.Connecting);

        OpenCampaignCommand = new AsyncRelayCommand(
            OpenCampaignAsync,
            () => _connection.State is ConnectionState.Connected or ConnectionState.Connecting);

        EditCampaignCommand = new AsyncRelayCommand(
            EditActiveCampaignAsync,
            () => CanEditOrUnregisterCampaign);

        UnregisterCampaignCommand = new AsyncRelayCommand(
            UnregisterActiveCampaignAsync,
            () => CanEditOrUnregisterCampaign);

        NewSpellCommand = new AsyncRelayCommand(
            NewSpellForActiveCampaignAsync,
            () => CanCreateCampaignScopedArtifact);

        NewPromptCommand = new AsyncRelayCommand(
            NewPromptForActiveCampaignAsync,
            () => CanCreateCampaignScopedArtifact);

        _activeCampaign.ActiveCampaignChanged += (_, _) => RaiseCanExecuteChanged();

        _connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IArcanumConnection.State))
            {
                RaiseCanExecuteChanged();
            }
        };

    }

    public IAsyncRelayCommand NewCampaignCommand { get; }

    public IAsyncRelayCommand OpenCampaignCommand { get; }

    public IAsyncRelayCommand EditCampaignCommand { get; }

    public IAsyncRelayCommand UnregisterCampaignCommand { get; }

    public IAsyncRelayCommand NewSpellCommand { get; }

    public IAsyncRelayCommand NewPromptCommand { get; }

    public bool CanEditOrUnregisterCampaign => ResolveAuthoringCampaign() is not null;

    public bool CanCreateCampaignScopedArtifact => CanEditOrUnregisterCampaign;

    public Func<Guid, CancellationToken, Task>? FocusCampaignInAtelierAsync { get; set; }

    public event EventHandler? CanEditOrUnregisterChanged;

    public async Task NewCampaignAsync(CancellationToken cancellationToken = default)
    {

        if (!EnsureArcanumReachable())
        {

            return;

        }

        bool loopback = ArcanumHostLocality.IsLoopbackBaseUrl(_settings.CurrentValue.BaseUrl);

        NewCampaignInputs? inputs = await _dialogs
            .PromptNewCampaignAsync(
                new NewCampaignDialogOptions(
                    AllowLocalFolderBrowse: loopback,
                    PathFieldLabel: loopback ? "Path" : "Path on Arcanum host",
                    IntroText: loopback
                        ? "Create a campaign. Choose a local folder or type an absolute path."
                        : "Create a campaign. Enter the absolute path on the Arcanum host."),
                cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        await RegisterAndFocusAsync(inputs, loopback, cancellationToken).ConfigureAwait(true);

    }

    public async Task OpenCampaignAsync(CancellationToken cancellationToken = default)
    {

        if (!EnsureArcanumReachable())
        {

            return;

        }

        bool loopback = ArcanumHostLocality.IsLoopbackBaseUrl(_settings.CurrentValue.BaseUrl);

        string? path = await _dialogs
            .PromptOpenCampaignPathAsync(loopback, cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        if (!CampaignPathComparer.TryNormalize(path, loopback, out string normalized, out string? normalizeError))
        {

            _whispers.Show(WhisperSeverity.Warning, normalizeError ?? "Invalid path.");

            _foundryFloor.AppendLine($"Open Campaign path invalid: {normalizeError}");

            return;

        }

        IReadOnlyList<CampaignDto> campaigns = await _atelierData
            .GetCampaignsAsync(cancellationToken)
            .ConfigureAwait(true);

        CampaignDto? existing = CampaignPathComparer.FindUnambiguousMatch(campaigns, normalized, loopback);

        if (existing is not null)
        {

            await FocusExistingAsync(existing, announceOpen: true, cancellationToken).ConfigureAwait(true);

            return;

        }

        string? proposedName = CampaignPathComparer.ProposeNameFromPath(normalized, loopback);

        NewCampaignInputs? inputs = await _dialogs
            .PromptNewCampaignAsync(
                new NewCampaignDialogOptions(
                    PrefillName: proposedName,
                    PrefillPath: normalized,
                    AllowLocalFolderBrowse: loopback,
                    PathFieldLabel: loopback ? "Path" : "Path on Arcanum host",
                    IntroText: proposedName is null
                        ? "No folder name could be derived from this path. Enter a campaign name."
                        : "Confirm campaign details for this folder."),
                cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        await RegisterAndFocusAsync(inputs, loopback, cancellationToken).ConfigureAwait(true);

    }

    public async Task EditActiveCampaignAsync(CancellationToken cancellationToken = default)
    {

        CampaignDto? active = ResolveAuthoringCampaign();

        if (active is null)
        {

            _whispers.Show(WhisperSeverity.Warning, "Select a campaign first.");

            return;

        }

        EditCampaignInputs? inputs = await _dialogs
            .PromptEditCampaignAsync(active, cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        UpdateCampaignRequest request = new(
            Name: inputs.Name,
            Type: inputs.Type,
            Description: inputs.Description,
            Settings: null);

        DataSourceResult<CampaignDto> result = await _management
            .UpdateAsync(active.Id, request, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success || result.Data is null)
        {

            string detail = FormatError(result.ErrorCode, result.ErrorMessage, "Failed to update campaign.");

            _foundryFloor.AppendLine($"Campaign update failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign update failed.");

            return;

        }

        await _activeCampaign.SetActiveCampaignAsync(result.Data, cancellationToken).ConfigureAwait(true);

        _foundryFloor.AppendLine($"Campaign updated: {result.Data.Name} ({result.Data.Id:D}).");

        _whispers.Show(WhisperSeverity.Success, "Campaign updated.");

        if (FocusCampaignInAtelierAsync is { } focus)
        {

            await focus(result.Data.Id, cancellationToken).ConfigureAwait(true);

        }

    }

    public async Task UnregisterActiveCampaignAsync(CancellationToken cancellationToken = default)
    {

        CampaignDto? active = ResolveAuthoringCampaign();

        if (active is null)
        {

            _whispers.Show(WhisperSeverity.Warning, "Select a campaign first.");

            return;

        }

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Unregister Campaign",
                $"Unregister campaign \"{active.Name}\"? This removes it from the registry only — disk files remain.",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        DataSourceResult<bool> result = await _management
            .DeleteAsync(active.Id, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success)
        {

            string detail = FormatError(result.ErrorCode, result.ErrorMessage, "Failed to unregister campaign.");

            _foundryFloor.AppendLine($"Campaign unregister failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign unregister failed.");

            return;

        }

        await _activeCampaign.SetActiveCampaignAsync(null, cancellationToken).ConfigureAwait(true);

        _foundryFloor.AppendLine($"Campaign unregistered: {active.Name} ({active.Id:D}).");

        _whispers.Show(WhisperSeverity.Success, "Campaign unregistered.");

        if (FocusCampaignInAtelierAsync is { } focus)
        {

            await focus(Guid.Empty, cancellationToken).ConfigureAwait(true);

        }

    }

    public async Task NewSpellForActiveCampaignAsync(CancellationToken cancellationToken = default)
    {

        CampaignDto? campaign = ResolveAuthoringCampaign();

        if (campaign is null)
        {

            _whispers.Show(WhisperSeverity.Warning, "Select or create a campaign first.");

            return;

        }

        WorkspaceOption preselected = new(campaign.Path, $"Campaign: {campaign.Name}");

        IReadOnlyList<WorkspaceOption> workspaces = await _creationDataSource
            .ListWorkspaceOptionsAsync(cancellationToken)
            .ConfigureAwait(true);

        NewSpellInputs? inputs = await _creationDialogs
            .PromptNewSpellAsync(workspaces, preselected, cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        CreateSpellRequest request = new(
            Name: inputs.Name,
            Description: inputs.Description,
            Tags: [],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: [],
            Body: inputs.Body);

        (bool success, string? error) = await _creationDataSource
            .CreateSpellAsync(inputs.WorkspacePath, request, cancellationToken)
            .ConfigureAwait(true);

        if (!success)
        {

            _foundryFloor.AppendLine($"New Spell failed: {error ?? "Failed to create spell."}");

            _whispers.Show(WhisperSeverity.Error, "Spell create failed.");

            return;

        }

        _foundryFloor.AppendLine($"Spell created: {inputs.Name}.");

        _whispers.Show(WhisperSeverity.Success, "Spell created.");

        if (FocusCampaignInAtelierAsync is { } focus)
        {

            await focus(campaign.Id, cancellationToken).ConfigureAwait(true);

        }

        _navigation.OpenDocument(DocumentKind.Spell, inputs.Name, inputs.WorkspacePath);

    }

    public async Task NewPromptForActiveCampaignAsync(CancellationToken cancellationToken = default)
    {

        CampaignDto? campaign = ResolveAuthoringCampaign();

        if (campaign is null)
        {

            _whispers.Show(WhisperSeverity.Warning, "Select or create a campaign first.");

            return;

        }

        NewPromptInputs? inputs = await _creationDialogs
            .PromptNewPromptAsync(campaign.Id, campaign.Name, cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        CreatePromptRequest request = new(
            Name: inputs.Name,
            Version: inputs.Version,
            Template: inputs.Template,
            Description: inputs.Description,
            Tags: null,
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            CampaignId: campaign.Id);

        (PromptDetailDto? prompt, string? error) = await _creationDataSource
            .CreatePromptAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (prompt is null)
        {

            _foundryFloor.AppendLine($"New Prompt failed: {error ?? "Failed to create prompt."}");

            _whispers.Show(WhisperSeverity.Error, "Prompt create failed.");

            return;

        }

        _foundryFloor.AppendLine($"Prompt created: {prompt.Name} {prompt.Version}.");

        _whispers.Show(WhisperSeverity.Success, "Prompt created.");

        if (FocusCampaignInAtelierAsync is { } focus)
        {

            await focus(campaign.Id, cancellationToken).ConfigureAwait(true);

        }

        _navigation.OpenDocument(DocumentKind.Prompt, prompt.Id.ToString("D"));

    }

    private CampaignDto? ResolveAuthoringCampaign()
    {

        CampaignDto? campaign = _activeCampaign.ActiveCampaign;

        if (campaign is null || campaign.Id == Guid.Empty || string.IsNullOrEmpty(campaign.Path))
        {

            return null;

        }

        return campaign;

    }

    private async Task RegisterAndFocusAsync(
        NewCampaignInputs inputs,
        bool loopback,
        CancellationToken cancellationToken)
    {

        if (!CampaignPathComparer.TryNormalize(inputs.Path, loopback, out string normalized, out string? normalizeError))
        {

            if (!loopback && !string.IsNullOrWhiteSpace(inputs.Path))
            {

                normalized = inputs.Path.Trim();

            }
            else
            {

                _whispers.Show(WhisperSeverity.Warning, normalizeError ?? "Invalid path.");

                _foundryFloor.AppendLine($"Campaign path invalid: {normalizeError}");

                return;

            }

        }

        RegisterCampaignRequest request = new(
            inputs.Name,
            normalized,
            inputs.Type,
            inputs.Description);

        DataSourceResult<CampaignDto> result = await _management
            .CreateAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success || result.Data is null)
        {

            if (IsDuplicatePath(result.ErrorCode))
            {

                IReadOnlyList<CampaignDto> campaigns = await _atelierData
                    .GetCampaignsAsync(cancellationToken)
                    .ConfigureAwait(true);

                CampaignDto? match = CampaignPathComparer.FindUnambiguousMatch(campaigns, normalized, loopback);

                if (match is not null)
                {

                    await FocusExistingAsync(match, announceOpen: true, cancellationToken).ConfigureAwait(true);

                    return;

                }

            }

            string detail = FormatError(result.ErrorCode, result.ErrorMessage, "Failed to create campaign.");

            _foundryFloor.AppendLine($"Campaign create failed: {detail}");

            string whisper = IsPathNotAllowed(result.ErrorCode)
                ? $"{detail} Add the folder’s parent to Arcanum:Campaigns:AllowedRoots (Compendium → The Forge → Campaigns allowed roots), then retry."
                : $"Campaign create failed. {detail}";

            _whispers.Show(WhisperSeverity.Error, whisper);

            if (IsAuthFailure(result.ErrorCode))
            {

                _connection.Connect();

            }

            return;

        }

        await FocusExistingAsync(result.Data, announceOpen: false, cancellationToken).ConfigureAwait(true);

        _foundryFloor.AppendLine($"Campaign created: {result.Data.Name} ({result.Data.Id:D}).");

        _whispers.Show(WhisperSeverity.Success, "Campaign created.");

    }

    private async Task FocusExistingAsync(CampaignDto campaign, bool announceOpen, CancellationToken cancellationToken)
    {

        await _activeCampaign.SetActiveCampaignAsync(campaign, cancellationToken).ConfigureAwait(true);

        if (FocusCampaignInAtelierAsync is { } focus)
        {

            await focus(campaign.Id, cancellationToken).ConfigureAwait(true);

        }

        _foundryFloor.AppendLine($"Campaign focused: {campaign.Name} ({campaign.Id:D}).");

        if (announceOpen)
        {

            _whispers.Show(WhisperSeverity.Success, $"Opened {campaign.Name}.");

        }

    }

    private bool EnsureArcanumReachable()
    {

        if (_connection.State is ConnectionState.Connected or ConnectionState.Connecting)
        {

            return true;

        }

        string detail = !string.IsNullOrWhiteSpace(_connection.LastErrorMessage)
            ? _connection.LastErrorMessage!
            : _connection.State == ConnectionState.Error
                ? "The Anvil shows Arcanum is unreachable — fix the connection, then retry."
                : "Use Connect on The Anvil or View → Connect to Arcanum.";

        _whispers.Show(WhisperSeverity.Warning, $"Connect to Arcanum first. {detail}");

        return false;

    }

    private static bool IsAuthFailure(string? code) =>
        string.Equals(code, "Security.MissingApiKey", StringComparison.Ordinal)
        || string.Equals(code, "Auth.Unauthorized", StringComparison.Ordinal);

    private void RaiseCanExecuteChanged()
    {

        NewCampaignCommand.NotifyCanExecuteChanged();

        OpenCampaignCommand.NotifyCanExecuteChanged();

        EditCampaignCommand.NotifyCanExecuteChanged();

        UnregisterCampaignCommand.NotifyCanExecuteChanged();

        NewSpellCommand.NotifyCanExecuteChanged();

        NewPromptCommand.NotifyCanExecuteChanged();

        CanEditOrUnregisterChanged?.Invoke(this, EventArgs.Empty);

    }

    private static bool IsDuplicatePath(string? code) =>
        string.Equals(code, "Campaign.DuplicatePath", StringComparison.Ordinal);

    private static bool IsPathNotAllowed(string? code) =>
        string.Equals(code, "Campaign.PathNotAllowed", StringComparison.Ordinal);

    private static string FormatError(string? code, string? message, string fallback)
    {

        if (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message))
        {

            return $"{code}: {message}";

        }

        if (!string.IsNullOrWhiteSpace(code))
        {

            return code;

        }

        return message ?? fallback;

    }

}
