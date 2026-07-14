using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Top-level "Global Prompts" root. Lazy-loads prompts with no campaign
/// (<c>PromptService.ListAsync(campaignId: null)</c> returns only <c>CampaignId IS NULL</c> prompts)
/// and exposes a New Prompt command that creates a global prompt (<c>CampaignId: null</c>).
/// </summary>
public sealed partial class GlobalPromptsRootNodeViewModel : AtelierNodeViewModel
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly FoundryFloorViewModel _foundryFloor;

    public GlobalPromptsRootNodeViewModel(
        IAtelierDataSource dataSource,
        INavigationService navigation,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService dialogService,
        FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _navigation = navigation;

        _creationDataSource = creationDataSource;

        _dialogService = dialogService;

        _foundryFloor = foundryFloor;

        Label = "Global Prompts";

        Icon = "IconPrompt";

        // Manual override (not [RelayCommand]) so HasNewPrompt sees the real command.
        NewPromptCommand = new AsyncRelayCommand(NewPromptAsync);

    }

    public override IAsyncRelayCommand? NewPromptCommand { get; }

    public override string? NewPromptLabel => "New Prompt";

    private async Task NewPromptAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        NewPromptInputs? inputs = await _dialogService
            .PromptNewPromptAsync(campaignId: null, campaignName: null, cancellationToken)
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
            CampaignId: null);

        (PromptDetailDto? prompt, string? error) = await _creationDataSource
            .CreatePromptAsync(request, cancellationToken)
            .ConfigureAwait(true);

        if (prompt is null)
        {

            LastError = error ?? "Failed to create prompt.";

            _foundryFloor.AppendLine($"Global New Prompt failed: {LastError}");

            return;

        }

        StatusText = "Prompt created.";

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Prompt, prompt.Id.ToString());

    }

    protected override async Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken)
    {

        return (await _dataSource.GetGlobalPromptsAsync(cancellationToken).ConfigureAwait(true))
            .OrderBy(static prompt => prompt.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static prompt => prompt.Version, StringComparer.OrdinalIgnoreCase)
            .Select(prompt => new PromptNodeViewModel(prompt, _navigation))
            .Cast<AtelierNodeViewModel>()
            .ToArray();

    }

}
