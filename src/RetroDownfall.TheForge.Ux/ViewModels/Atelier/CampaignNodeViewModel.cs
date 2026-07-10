using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Campaign branch. It lazy-loads campaign-scoped spells, prompts, sessions, CODEX.md, and Sanctum
/// on first expansion. Create dialogs are not implemented yet — New* commands are disabled.
/// </summary>
public sealed partial class CampaignNodeViewModel : AtelierNodeViewModel
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    public CampaignNodeViewModel(CampaignDto campaign, IAtelierDataSource dataSource, INavigationService navigation)
    {

        Campaign = campaign;

        _dataSource = dataSource;

        _navigation = navigation;

        Label = campaign.Name;

        Icon = "IconCampaign";

    }

    public CampaignDto Campaign { get; }

    /// <summary>Create-spell dialog is not implemented yet.</summary>
    public bool CanCreateDocuments => false;

    [RelayCommand(CanExecute = nameof(CanCreateDocuments))]
    private void NewSpell()
    {

        // Intentionally disabled until create dialogs/API flows exist.
        // Do not open fake "new:{id}" documents.

    }

    [RelayCommand(CanExecute = nameof(CanCreateDocuments))]
    private void NewPrompt()
    {

        // Intentionally disabled until create dialogs/API flows exist.

    }

    [RelayCommand(CanExecute = nameof(CanCreateDocuments))]
    private void NewSession()
    {

        // Intentionally disabled until create dialogs/API flows exist.

    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => ReloadAsync(cancellationToken);

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
            new StaticAtelierNodeViewModel("CODEX.md", "IconCodex"),
            new StaticAtelierNodeViewModel("Sanctum", "IconSanctum"),
        ];

    }

}
