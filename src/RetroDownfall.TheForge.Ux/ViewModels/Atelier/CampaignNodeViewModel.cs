using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Campaign branch. Lazy-loads campaign-scoped spells, prompts, sessions, CODEX.md, and Sanctum on
/// first expansion; exposes New Spell / New Prompt / New Session plus Edit / Delete / Export / Import.
/// Open marks the campaign active and opens its Codex.
/// </summary>
public sealed partial class CampaignNodeViewModel : AtelierNodeViewModel
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly IActiveCampaignService _activeCampaign;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly ICampaignManagementDataSource _management;

    private readonly ICampaignDialogService _campaignDialog;

    private readonly IConfirmationDialogService _confirmation;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly IWhispersService _whispers;

    private readonly Func<CancellationToken, Task> _refreshCampaigns;

    private CampaignDto _campaign;

    public CampaignNodeViewModel(
        CampaignDto campaign,
        IAtelierDataSource dataSource,
        INavigationService navigation,
        IActiveCampaignService activeCampaign,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService dialogService,
        FoundryFloorViewModel foundryFloor,
        ICampaignManagementDataSource management,
        ICampaignDialogService campaignDialog,
        IConfirmationDialogService confirmation,
        IArtifactFileDialogService fileDialog,
        IWhispersService whispers,
        Func<CancellationToken, Task> refreshCampaigns)
    {

        _campaign = campaign;

        _dataSource = dataSource;

        _navigation = navigation;

        _activeCampaign = activeCampaign;

        _creationDataSource = creationDataSource;

        _dialogService = dialogService;

        _foundryFloor = foundryFloor;

        _management = management;

        _campaignDialog = campaignDialog;

        _confirmation = confirmation;

        _fileDialog = fileDialog;

        _whispers = whispers;

        _refreshCampaigns = refreshCampaigns;

        Label = campaign.Name;

        Icon = "IconCampaign";

        NewSpellCommand = new AsyncRelayCommand(NewSpellAsync);

        NewPromptCommand = new AsyncRelayCommand(NewPromptAsync);

        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync);

        EditCampaignCommand = new AsyncRelayCommand(EditCampaignAsync);

        DeleteCampaignCommand = new AsyncRelayCommand(DeleteCampaignAsync);

        ExportCampaignCommand = new AsyncRelayCommand(ExportCampaignAsync);

        ImportCampaignCommand = new AsyncRelayCommand(ImportCampaignAsync);

    }

    public CampaignDto Campaign => _campaign;

    public override ICommand? PrimaryCommand => OpenCommand;

    public override IAsyncRelayCommand? NewSpellCommand { get; }

    public override IAsyncRelayCommand? NewPromptCommand { get; }

    public override IAsyncRelayCommand? NewSessionCommand { get; }

    public override IAsyncRelayCommand? EditCampaignCommand { get; }

    public override IAsyncRelayCommand? DeleteCampaignCommand { get; }

    public override IAsyncRelayCommand? ExportCampaignCommand { get; }

    public override IAsyncRelayCommand? ImportCampaignCommand { get; }

    public override string? NewSpellLabel => "New Spell";

    public override string? NewPromptLabel => "New Prompt";

    public override string? NewSessionLabel => "New Session";

    [RelayCommand]
    private async Task OpenAsync(CancellationToken cancellationToken)
    {

        await _activeCampaign.SetActiveCampaignAsync(_campaign, cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Codex, _campaign.Id.ToString("D"));

    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

    private async Task EditCampaignAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        EditCampaignInputs? inputs = await _campaignDialog
            .PromptEditCampaignAsync(_campaign, cancellationToken)
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
            .UpdateAsync(_campaign.Id, request, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success || result.Data is null)
        {

            string detail = CampaignsRootNodeViewModel.FormatCampaignError(
                result.ErrorCode,
                result.ErrorMessage,
                "Failed to update campaign.");

            LastError = detail;

            _foundryFloor.AppendLine($"Campaign update failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign update failed.");

            return;

        }

        _campaign = result.Data;

        Label = result.Data.Name;

        StatusText = "Campaign updated.";

        _foundryFloor.AppendLine($"Campaign updated: {result.Data.Name} ({result.Data.Id:D}).");

        _whispers.Show(WhisperSeverity.Success, "Campaign updated.");

    }

    private async Task DeleteCampaignAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Unregister Campaign",
                $"Unregister campaign \"{_campaign.Name}\"? This removes it from the registry only — disk files remain.",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        DataSourceResult<bool> result;

        try
        {

            result = await _management
                .DeleteAsync(_campaign.Id, cancellationToken)
                .ConfigureAwait(true);

        }
        catch (OperationCanceledException)
        {

            // Closing the Atelier mid-unregister cancels the caller's token. Unhandled it escapes
            // onto the dispatcher from a fire-and-forget command instead of ending the operation.
            StatusText = "Campaign unregister cancelled.";

            return;

        }

        if (!result.Success)
        {

            string detail = CampaignsRootNodeViewModel.FormatCampaignError(
                result.ErrorCode,
                result.ErrorMessage,
                "Failed to unregister campaign.");

            LastError = detail;

            _foundryFloor.AppendLine($"Campaign delete failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign delete failed.");

            return;

        }

        StatusText = "Campaign unregistered.";

        _foundryFloor.AppendLine($"Campaign unregistered: {_campaign.Name} ({_campaign.Id:D}). Disk files were not deleted.");

        _whispers.Show(WhisperSeverity.Success, "Campaign unregistered.");

        await _refreshCampaigns(cancellationToken).ConfigureAwait(true);

    }

    private async Task ExportCampaignAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        string? path = await ArtifactImportExportHelper
            .PickSavePathOrNullAsync(_fileDialog, $"{_campaign.Name}.json", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        DataSourceResult<CampaignExportDto> result = await _management
            .ExportAsync(_campaign.Id, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success || result.Data is null)
        {

            string detail = CampaignsRootNodeViewModel.FormatCampaignError(
                result.ErrorCode,
                result.ErrorMessage,
                "Failed to export campaign.");

            LastError = detail;

            _foundryFloor.AppendLine($"Campaign export failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign export failed.");

            return;

        }

        string? writeError = await ArtifactImportExportHelper
            .WriteJsonAsync(path, result.Data, TheForgeJsonContext.Default.CampaignExportDto, cancellationToken)
            .ConfigureAwait(true);

        if (writeError is not null)
        {

            LastError = writeError;

            _foundryFloor.AppendLine($"Campaign export write error: {writeError}");

            _whispers.Show(WhisperSeverity.Error, "Campaign export failed.");

            return;

        }

        StatusText = "Campaign exported.";

        _foundryFloor.AppendLine($"Campaign exported: {_campaign.Name} → {path}.");

        _whispers.Show(WhisperSeverity.Success, "Campaign exported.");

    }

    private async Task ImportCampaignAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        string? strategy = await _campaignDialog
            .PromptImportStrategyAsync(cancellationToken)
            .ConfigureAwait(true);

        if (strategy is null)
        {

            return;

        }

        string? path = await ArtifactImportExportHelper
            .PickOpenPathOrNullAsync(_fileDialog, cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        (CampaignExportDto? payload, string? readError) = await ArtifactImportExportHelper
            .ReadJsonAsync(path, TheForgeJsonContext.Default.CampaignExportDto, cancellationToken)
            .ConfigureAwait(true);

        if (payload is null)
        {

            LastError = readError ?? "Failed to read campaign export JSON.";

            _foundryFloor.AppendLine($"Campaign import read failed: {LastError}");

            _whispers.Show(WhisperSeverity.Error, "Campaign import failed.");

            return;

        }

        DataSourceResult<CampaignImportResultDto> result = await _management
            .ImportAsync(_campaign.Id, strategy, payload, cancellationToken)
            .ConfigureAwait(true);

        if (!result.Success || result.Data is null)
        {

            string detail = CampaignsRootNodeViewModel.FormatCampaignError(
                result.ErrorCode,
                result.ErrorMessage,
                "Failed to import into campaign.");

            LastError = detail;

            _foundryFloor.AppendLine($"Campaign import failed: {detail}");

            _whispers.Show(WhisperSeverity.Error, "Campaign import failed.");

            return;

        }

        CampaignImportResultDto imported = result.Data;

        StatusText = "Campaign imported.";

        _foundryFloor.AppendLine(
            $"Campaign import ({strategy}): spells={imported.SpellsImported}, prompts={imported.PromptsImported}, warnings={imported.Warnings.Count}.");

        foreach (string warning in imported.Warnings)
        {

            _foundryFloor.AppendLine($"Campaign import warning: {warning}");

        }

        _whispers.Show(
            WhisperSeverity.Success,
            $"Imported {imported.SpellsImported} spells, {imported.PromptsImported} prompts.");

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

    }

    private async Task NewSpellAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        WorkspaceOption preselected = new(Campaign.Path, $"Campaign: {Campaign.Name}");

        IReadOnlyList<WorkspaceOption> workspaces = await _creationDataSource
            .ListWorkspaceOptionsAsync(cancellationToken)
            .ConfigureAwait(true);

        NewSpellInputs? inputs = await _dialogService
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

            LastError = error ?? "Failed to create spell.";

            _foundryFloor.AppendLine($"Campaign New Spell failed: {LastError}");

            return;

        }

        StatusText = "Spell created.";

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Spell, inputs.Name, inputs.WorkspacePath);

    }

    private async Task NewPromptAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        NewPromptInputs? inputs = await _dialogService
            .PromptNewPromptAsync(Campaign.Id, Campaign.Name, cancellationToken)
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
            CampaignId: Campaign.Id);

        (PromptDetailDto? prompt, string? error) = await _creationDataSource
            .CreatePromptAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (prompt is null)
        {

            LastError = error ?? "Failed to create prompt.";

            _foundryFloor.AppendLine($"Campaign New Prompt failed: {LastError}");

            return;

        }

        StatusText = "Prompt created.";

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Prompt, prompt.Id.ToString());

    }

    private async Task NewSessionAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        NewSessionInputs? inputs = await _dialogService
            .PromptNewSessionAsync(Campaign.Id, Campaign.Name, cancellationToken)
            .ConfigureAwait(true);

        if (inputs is null)
        {

            return;

        }

        CreateSessionRequest request = new(CampaignId: Campaign.Id, Title: inputs.Title);

        (SessionDetailDto? session, string? error) = await _creationDataSource
            .CreateSessionAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (session is null)
        {

            LastError = error ?? "Failed to create session.";

            _foundryFloor.AppendLine($"Campaign New Session failed: {LastError}");

            return;

        }

        StatusText = "Session created.";

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Session, session.Id.ToString("D"));

    }

    protected override async Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<AtelierNodeViewModel> spellNodes = (await _dataSource
                .GetCampaignSpellsAsync(Campaign.Id, cancellationToken)
                .ConfigureAwait(true))
            .OrderBy(static spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .Select(spell => new SpellNodeViewModel(spell, _navigation, Campaign.Path))
            .Cast<AtelierNodeViewModel>()
            .ToArray();

        IReadOnlyList<AtelierNodeViewModel> promptNodes = (await _dataSource
                .GetCampaignPromptsAsync(Campaign.Id, cancellationToken)
                .ConfigureAwait(true))
            .OrderBy(static prompt => prompt.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.Version, StringComparer.OrdinalIgnoreCase)
            .Select(prompt => new PromptNodeViewModel(prompt, _navigation))
            .Cast<AtelierNodeViewModel>()
            .ToArray();

        IReadOnlyList<AtelierNodeViewModel> sessionNodes = (await _dataSource
                .GetCampaignSessionsAsync(Campaign.Id, cancellationToken)
                .ConfigureAwait(true))
            .OrderByDescending(static session => session.UpdatedAt)
            .Select(session => new SessionNodeViewModel(session, _navigation))
            .Cast<AtelierNodeViewModel>()
            .ToArray();

        return
        [
            new AtelierCategoryNodeViewModel("Spells", "IconSpell", spellNodes),
            new AtelierCategoryNodeViewModel("Prompts", "IconPrompt", promptNodes),
            new AtelierCategoryNodeViewModel("Sessions", "IconSession", sessionNodes),
            new CodexNodeViewModel(Campaign, _navigation),
            new StaticAtelierNodeViewModel("Sanctum", "IconSanctum"),
        ];

    }

}
