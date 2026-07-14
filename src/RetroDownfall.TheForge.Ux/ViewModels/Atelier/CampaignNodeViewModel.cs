using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Campaign branch. Lazy-loads campaign-scoped spells, prompts, sessions, CODEX.md, and Sanctum on
/// first expansion, and exposes New Spell / New Prompt / New Session commands that create artifacts
/// scoped to the campaign and open them in the Workbench.
/// </summary>
public sealed partial class CampaignNodeViewModel : AtelierNodeViewModel
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly FoundryFloorViewModel _foundryFloor;

    public CampaignNodeViewModel(
        CampaignDto campaign,
        IAtelierDataSource dataSource,
        INavigationService navigation,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService dialogService,
        FoundryFloorViewModel foundryFloor)
    {

        Campaign = campaign;

        _dataSource = dataSource;

        _navigation = navigation;

        _creationDataSource = creationDataSource;

        _dialogService = dialogService;

        _foundryFloor = foundryFloor;

        Label = campaign.Name;

        Icon = "IconCampaign";

        // Manual override (not [RelayCommand]) so the base HasNew* properties see the real commands.
        NewSpellCommand = new AsyncRelayCommand(NewSpellAsync);

        NewPromptCommand = new AsyncRelayCommand(NewPromptAsync);

        NewSessionCommand = new AsyncRelayCommand(NewSessionAsync);

    }

    public CampaignDto Campaign { get; }

    public override IAsyncRelayCommand? NewSpellCommand { get; }

    public override IAsyncRelayCommand? NewPromptCommand { get; }

    public override IAsyncRelayCommand? NewSessionCommand { get; }

    public override string? NewSpellLabel => "New Spell";

    public override string? NewPromptLabel => "New Prompt";

    public override string? NewSessionLabel => "New Session";

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

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

        _navigation.OpenDocument(DocumentKind.Spell, inputs.Name);

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
            .Select(spell => new SpellNodeViewModel(spell, _navigation))
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
