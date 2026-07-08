using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Root ViewModel for The Atelier (project explorer). Phase 4 establishes the live tree roots and
/// lazy-loading node model; later phases add creation/deletion/export dialogs behind the existing
/// context-menu command seams.
/// </summary>
public sealed partial class AtelierViewModel : ViewModelBase
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    [ObservableProperty]
    private bool _isLoading;

    public AtelierViewModel(IAtelierDataSource dataSource, INavigationService navigation)
    {

        _dataSource = dataSource;

        _navigation = navigation;

        Title = "The Atelier";

    }

    public ObservableCollection<AtelierNodeViewModel> Roots { get; } = [];

    public string EmptyState => Roots.Count == 0
        ? "Refresh The Atelier to reveal campaigns, workspaces, spells, and sessions."
        : string.Empty;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsLoading = true;

        try
        {

            Roots.Clear();

            Roots.Add(CreateCampaignsRoot());

            Roots.Add(CreateWorkspacesRoot());

            Roots.Add(CreateGlobalSpellsRoot());

            Roots.Add(CreateSessionsRoot());

            OnPropertyChanged(nameof(EmptyState));

        }
        finally
        {

            IsLoading = false;

        }

    }

    private AtelierRootNodeViewModel CreateCampaignsRoot() =>
        new(
            "Campaigns",
            "IconCampaign",
            async ct => (await _dataSource.GetCampaignsAsync(ct).ConfigureAwait(true))
                .OrderBy(static campaign => campaign.Name, StringComparer.OrdinalIgnoreCase)
                .Select(campaign => new CampaignNodeViewModel(campaign, _dataSource, _navigation))
                .Cast<AtelierNodeViewModel>()
                .ToArray());

    private AtelierRootNodeViewModel CreateWorkspacesRoot() =>
        new(
            "Workspaces",
            "IconWorkspace",
            async ct => (await _dataSource.GetWorkspacesAsync(ct).ConfigureAwait(true))
                .OrderBy(static workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static workspace => new WorkspaceNodeViewModel(workspace))
                .Cast<AtelierNodeViewModel>()
                .ToArray());

    private AtelierRootNodeViewModel CreateGlobalSpellsRoot() =>
        new(
            "Global Spells",
            "IconSpell",
            async ct => (await _dataSource.GetGlobalSpellsAsync(ct).ConfigureAwait(true))
                .OrderBy(static spell => spell.Name, StringComparer.OrdinalIgnoreCase)
                .Select(spell => new SpellNodeViewModel(spell, _navigation))
                .Cast<AtelierNodeViewModel>()
                .ToArray());

    private AtelierRootNodeViewModel CreateSessionsRoot() =>
        new(
            "Sessions",
            "IconSession",
            async ct => (await _dataSource.GetRecentSessionsAsync(ct).ConfigureAwait(true))
                .OrderByDescending(static session => session.UpdatedAt)
                .Select(session => new SessionNodeViewModel(session, _navigation))
                .Cast<AtelierNodeViewModel>()
                .ToArray());

}
