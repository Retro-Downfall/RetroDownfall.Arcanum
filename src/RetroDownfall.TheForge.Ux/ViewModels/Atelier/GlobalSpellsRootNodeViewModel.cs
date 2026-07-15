using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Top-level "Global Spells" root. Lazy-loads built-in/workspace spells and exposes a
/// "New Workspace Spell…" command. A newly created workspace spell does not necessarily appear under
/// this root after reload — the success message says so honestly.
/// </summary>
public sealed partial class GlobalSpellsRootNodeViewModel : AtelierNodeViewModel
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly FoundryFloorViewModel _foundryFloor;

    public GlobalSpellsRootNodeViewModel(
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

        Label = "Global Spells";

        Icon = "IconSpell";

        // Manual override (not [RelayCommand]) so HasNewSpell sees the real command.
        NewSpellCommand = new AsyncRelayCommand(NewSpellAsync);

    }

    public override IAsyncRelayCommand? NewSpellCommand { get; }

    public override string? NewSpellLabel => "New Workspace Spell…";

    private async Task NewSpellAsync(CancellationToken cancellationToken)
    {

        LastError = null;

        IReadOnlyList<WorkspaceOption> workspaces = await _creationDataSource
            .ListWorkspaceOptionsAsync(cancellationToken)
            .ConfigureAwait(true);

        NewSpellInputs? inputs = await _dialogService
            .PromptNewSpellAsync(workspaces, preselected: null, cancellationToken)
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

            _foundryFloor.AppendLine($"New Workspace Spell failed: {LastError}");

            return;

        }

        StatusText = "Spell created in workspace. It may appear under the selected campaign/workspace tree after refresh.";

        await ReloadAsync(cancellationToken).ConfigureAwait(true);

        _navigation.OpenDocument(DocumentKind.Spell, inputs.Name, inputs.WorkspacePath);

    }

    protected override async Task<IReadOnlyList<AtelierNodeViewModel>> LoadChildrenAsync(CancellationToken cancellationToken)
    {

        return (await _dataSource.GetGlobalSpellsAsync(cancellationToken).ConfigureAwait(true))
            .OrderBy(static spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .Select(spell => new SpellNodeViewModel(spell, _navigation))
            .Cast<AtelierNodeViewModel>()
            .ToArray();

    }

}
