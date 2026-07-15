using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Root ViewModel for The Atelier (project explorer). Establishes the live tree roots
/// (Campaigns, Workspaces, Global Spells, Global Prompts, Sessions) with lazy-loading nodes, and
/// threads the creation and campaign-management seams into the campaign node and creation-capable roots.
/// </summary>
public sealed partial class AtelierViewModel : ViewModelBase
{

    private readonly IAtelierDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly ICampaignManagementDataSource _campaignManagement;

    private readonly ICampaignDialogService _campaignDialog;

    private readonly IConfirmationDialogService _confirmation;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly IWhispersService _whispers;

    private readonly FoundryFloorViewModel _foundryFloor;

    [ObservableProperty]
    private bool _isLoading;

    public AtelierViewModel(
        IAtelierDataSource dataSource,
        INavigationService navigation,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService dialogService,
        ICampaignManagementDataSource campaignManagement,
        ICampaignDialogService campaignDialog,
        IConfirmationDialogService confirmation,
        IArtifactFileDialogService fileDialog,
        IWhispersService whispers,
        FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _navigation = navigation;

        _creationDataSource = creationDataSource;

        _dialogService = dialogService;

        _campaignManagement = campaignManagement;

        _campaignDialog = campaignDialog;

        _confirmation = confirmation;

        _fileDialog = fileDialog;

        _whispers = whispers;

        _foundryFloor = foundryFloor;

        Title = "The Atelier";

    }

    public ObservableCollection<AtelierNodeViewModel> Roots { get; } = [];

    public string EmptyState => Roots.Count == 0
        ? "Refresh The Atelier to reveal campaigns, workspaces, spells, prompts, and sessions."
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

            Roots.Add(CreateGlobalPromptsRoot());

            Roots.Add(CreateSessionsRoot());

            OnPropertyChanged(nameof(EmptyState));

        }
        finally
        {

            IsLoading = false;

        }

    }

    private AtelierNodeViewModel CreateCampaignsRoot()
    {

        CampaignsRootNodeViewModel? root = null;

        root = new CampaignsRootNodeViewModel(
            _campaignManagement,
            _campaignDialog,
            _whispers,
            _foundryFloor,
            async ct =>
            {
                if (root is not null)
                {

                    await root.ReloadAsync(ct).ConfigureAwait(true);

                }
            },
            async ct => (await _dataSource.GetCampaignsAsync(ct).ConfigureAwait(true))
                .OrderBy(static campaign => campaign.Name, StringComparer.OrdinalIgnoreCase)
                .Select(campaign => new CampaignNodeViewModel(
                    campaign,
                    _dataSource,
                    _navigation,
                    _creationDataSource,
                    _dialogService,
                    _foundryFloor,
                    _campaignManagement,
                    _campaignDialog,
                    _confirmation,
                    _fileDialog,
                    _whispers,
                    async refreshCt =>
                    {
                        if (root is not null)
                        {

                            await root.ReloadAsync(refreshCt).ConfigureAwait(true);

                        }
                    }))
                .Cast<AtelierNodeViewModel>()
                .ToArray());

        return root;

    }

    private AtelierNodeViewModel CreateWorkspacesRoot() =>
        new AtelierRootNodeViewModel(
            "Workspaces",
            "IconWorkspace",
            async ct => (await _dataSource.GetWorkspacesAsync(ct).ConfigureAwait(true))
                .OrderBy(static workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
                .Select(workspace => new WorkspaceNodeViewModel(workspace, _navigation))
                .Cast<AtelierNodeViewModel>()
                .ToArray());

    private AtelierNodeViewModel CreateGlobalSpellsRoot() =>
        new GlobalSpellsRootNodeViewModel(_dataSource, _navigation, _creationDataSource, _dialogService, _foundryFloor);

    private AtelierNodeViewModel CreateGlobalPromptsRoot() =>
        new GlobalPromptsRootNodeViewModel(_dataSource, _navigation, _creationDataSource, _dialogService, _foundryFloor);

    private AtelierNodeViewModel CreateSessionsRoot() =>
        new SessionsRootNodeViewModel(_dataSource, _navigation, _creationDataSource, _dialogService, _foundryFloor);

}
