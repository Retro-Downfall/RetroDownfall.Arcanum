using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
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

    private readonly IActiveCampaignService _activeCampaign;

    private readonly ICampaignCommandCoordinator _campaignCommands;

    private readonly IArtifactCreationDataSource _creationDataSource;

    private readonly IArtifactCreationDialogService _dialogService;

    private readonly ICampaignManagementDataSource _campaignManagement;

    private readonly ICampaignDialogService _campaignDialog;

    private readonly IConfirmationDialogService _confirmation;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly IWhispersService _whispers;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IArcanumConnection _connection;

    private readonly ITheForgeLocalMutationRunner _mutationRunner;

    private int _campaignCount;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private AtelierNodeViewModel? _selectedNode;

    public AtelierViewModel(
        IAtelierDataSource dataSource,
        INavigationService navigation,
        IActiveCampaignService activeCampaign,
        ICampaignCommandCoordinator campaignCommands,
        IArtifactCreationDataSource creationDataSource,
        IArtifactCreationDialogService dialogService,
        ICampaignManagementDataSource campaignManagement,
        ICampaignDialogService campaignDialog,
        IConfirmationDialogService confirmation,
        IArtifactFileDialogService fileDialog,
        IWhispersService whispers,
        FoundryFloorViewModel foundryFloor,
        IArcanumConnection connection,
        ITheForgeLocalMutationRunner mutationRunner)
    {

        _dataSource = dataSource;

        _navigation = navigation;

        _activeCampaign = activeCampaign;

        _campaignCommands = campaignCommands;

        _creationDataSource = creationDataSource;

        _dialogService = dialogService;

        _campaignManagement = campaignManagement;

        _campaignDialog = campaignDialog;

        _confirmation = confirmation;

        _fileDialog = fileDialog;

        _whispers = whispers;

        _foundryFloor = foundryFloor;

        _connection = connection;

        _mutationRunner = mutationRunner;

        Title = "The Atelier";

        _campaignCommands.FocusCampaignInAtelierAsync = FocusCampaignAsync;

        _connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IArcanumConnection.State))
            {
                OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));
                OnPropertyChanged(nameof(ShowDisconnectedGuidance));
            }
        };

    }

    public ObservableCollection<AtelierNodeViewModel> Roots { get; } = [];

    public ICampaignCommandCoordinator CampaignCommands => _campaignCommands;

    /// <summary>
    /// Zero-campaign onboarding when connected — coexists with Workspaces / Global / Sessions roots.
    /// </summary>
    public bool ShowZeroCampaignOnboarding =>
        _connection.State == ConnectionState.Connected
        && Roots.Count > 0
        && _campaignCount == 0
        && !IsLoading;

    public bool ShowDisconnectedGuidance =>
        _connection.State != ConnectionState.Connected && Roots.Count > 0 && !IsLoading;

    public string ZeroCampaignMessage =>
        "A Campaign is the top-level workspace for spells, prompts, sessions, and Codex content.";

    public string DisconnectedGuidanceMessage =>
        "Connect to Arcanum to load campaigns, or open Setup to configure the connection.";

    public string EmptyState => Roots.Count == 0
        ? "Refresh The Atelier to reveal campaigns, workspaces, spells, prompts, and sessions."
        : string.Empty;

    partial void OnSelectedNodeChanged(AtelierNodeViewModel? value)
    {

        if (value is CampaignNodeViewModel campaignNode)
        {

            TaskUtilities.FireAndForget(ApplyActiveCampaignAsync(campaignNode.Campaign));

        }

    }

    /// <summary>
    /// Records the tree selection as the active campaign. The selection hook cannot await, so failures
    /// are reported here rather than left as an unobserved task.
    /// </summary>
    private async Task ApplyActiveCampaignAsync(CampaignDto campaign)
    {

        try
        {

            await _activeCampaign.SetActiveCampaignAsync(campaign).ConfigureAwait(true);

        }
        catch (OperationCanceledException)
        {

            // The selection was superseded before the active campaign could be recorded.

        }
        catch (Exception ex)
        {

            _foundryFloor.AppendLine($"Active campaign error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, $"Could not record “{campaign.Name}” as the active campaign.");

        }

    }

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

            await UpdateCampaignCountAsync(cancellationToken).ConfigureAwait(true);

            OnPropertyChanged(nameof(EmptyState));

            OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));

            OnPropertyChanged(nameof(ShowDisconnectedGuidance));

        }
        finally
        {

            IsLoading = false;

            OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));

            OnPropertyChanged(nameof(ShowDisconnectedGuidance));

        }

    }

    /// <summary>
    /// Reloads the Campaigns branch and selects/expands the campaign with the given id.
    /// When <paramref name="campaignId"/> is <see cref="Guid.Empty"/>, only refreshes the branch.
    /// </summary>
    public async Task<bool> FocusCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {

        if (Roots.Count == 0)
        {

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

        }

        AtelierNodeViewModel? campaignsRoot = Roots.FirstOrDefault(static r => r.Label == "Campaigns");

        if (campaignsRoot is null)
        {

            return false;

        }

        await campaignsRoot.ReloadAsync(cancellationToken).ConfigureAwait(true);

        campaignsRoot.IsExpanded = true;

        await UpdateCampaignCountAsync(cancellationToken).ConfigureAwait(true);

        OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));

        if (campaignId == Guid.Empty)
        {

            return true;

        }

        CampaignNodeViewModel? node = campaignsRoot.Children
            .OfType<CampaignNodeViewModel>()
            .FirstOrDefault(c => c.Campaign.Id == campaignId);

        if (node is null)
        {

            CampaignDto? campaign = await _dataSource
                .GetCampaignAsync(campaignId, cancellationToken)
                .ConfigureAwait(true);

            if (campaign is null || campaign.Id != campaignId)
            {

                return false;

            }

            node = CreateCampaignNode(campaign, campaignsRoot);

            campaignsRoot.Children.Add(node);

            _campaignCount = Math.Max(_campaignCount, 1);

            OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));

        }

        _activeCampaign.HydrateIfMatching(node.Campaign);

        await _activeCampaign.SetActiveCampaignAsync(node.Campaign, cancellationToken).ConfigureAwait(true);

        SelectedNode = node;

        node.IsExpanded = true;

        return true;

    }

    private async Task UpdateCampaignCountAsync(CancellationToken cancellationToken)
    {

        IReadOnlyList<CampaignDto> campaigns = await _dataSource
            .GetCampaignsAsync(cancellationToken)
            .ConfigureAwait(true);

        _campaignCount = campaigns.Count;

        foreach (CampaignDto campaign in campaigns)
        {

            _activeCampaign.HydrateIfMatching(campaign);

        }

    }

    private AtelierNodeViewModel CreateCampaignsRoot()
    {

        CampaignsRootNodeViewModel? root = null;

        root = new CampaignsRootNodeViewModel(
            _campaignCommands,
            async ct =>
            {
                if (root is not null)
                {

                    await root.ReloadAsync(ct).ConfigureAwait(true);

                    await UpdateCampaignCountAsync(ct).ConfigureAwait(true);

                    OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));

                }
            },
            async ct =>
            {
                IReadOnlyList<CampaignDto> campaigns = (await _dataSource.GetCampaignsAsync(ct).ConfigureAwait(true))
                    .OrderBy(static campaign => campaign.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                _campaignCount = campaigns.Count;

                foreach (CampaignDto campaign in campaigns)
                {

                    _activeCampaign.HydrateIfMatching(campaign);

                }

                return campaigns
                    .Select(campaign => CreateCampaignNode(campaign, root!))
                    .Cast<AtelierNodeViewModel>()
                    .ToArray();
            });

        return root;

    }

    private CampaignNodeViewModel CreateCampaignNode(
        CampaignDto campaign,
        AtelierNodeViewModel campaignsRoot) =>
        new(
            campaign,
            _dataSource,
            _navigation,
            _activeCampaign,
            _creationDataSource,
            _dialogService,
            _foundryFloor,
            _campaignManagement,
            _campaignDialog,
            _confirmation,
            _fileDialog,
            _whispers,
            _mutationRunner,
            async refreshCt =>
            {

                await campaignsRoot.ReloadAsync(refreshCt).ConfigureAwait(true);

                await UpdateCampaignCountAsync(refreshCt).ConfigureAwait(true);

                OnPropertyChanged(nameof(ShowZeroCampaignOnboarding));

            });

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
