using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.TheForge.Ux.ViewModels.WarTable;

/// <summary>
/// The War Table lists apprentices, hosts detail/plan/lineage/chronicle for the selection, and
/// creates new apprentices. Visibility gates list refresh and chronicle streaming.
/// </summary>
public sealed partial class WarTableViewModel : ViewModelBase, IDisposable
{

    private readonly IWarTableDataSource _dataSource;

    private bool _disposed;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private ApprenticeDetailViewModel? _selectedApprentice;

    [ObservableProperty]
    private bool _isCreatePanelOpen;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newGoal = string.Empty;

    [ObservableProperty]
    private CampaignDto? _selectedCampaign;

    [ObservableProperty]
    private WorkspaceInfo? _selectedWorkspace;

    public WarTableViewModel(IWarTableDataSource dataSource)
    {

        _dataSource = dataSource;

        Title = "The War Table";

    }

    public ObservableCollection<ApprenticeSummaryDto> Apprentices { get; } = [];

    public ObservableCollection<CampaignDto> Campaigns { get; } = [];

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    public bool HasNoApprentices => Apprentices.Count == 0;

    public string EmptyState => "No apprentices yet — cast one to begin the Conclave.";

    partial void OnIsVisibleChanged(bool value)
    {

        if (value)
        {

            _ = RefreshCommand.ExecuteAsync(null);

            SelectedApprentice?.Activate();

        }
        else
        {

            SelectedApprentice?.Deactivate();

        }

    }

    partial void OnSelectedApprenticeChanged(ApprenticeDetailViewModel? oldValue, ApprenticeDetailViewModel? newValue)
    {

        oldValue?.Dispose();

        if (IsVisible)
        {

            newValue?.Activate();

        }

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            IReadOnlyList<ApprenticeSummaryDto> list = await _dataSource.ListApprenticesAsync(cancellationToken).ConfigureAwait(true);

            Apprentices.Clear();

            foreach (ApprenticeSummaryDto item in list)
            {

                Apprentices.Add(item);

            }

            OnPropertyChanged(nameof(HasNoApprentices));

            if (SelectedApprentice is not null)
            {

                await SelectedApprentice.LoadAsync(cancellationToken).ConfigureAwait(true);

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task SelectApprenticeAsync(ApprenticeSummaryDto? summary, CancellationToken cancellationToken)
    {

        if (summary is null)
        {

            return;

        }

        ApprenticeDetailViewModel detail = new(summary.Id, _dataSource);

        // OnSelectedApprenticeChanged owns activation while the panel is visible. Activating again
        // here would cancel the Chronicle stream that assignment just opened and reopen a second one,
        // replaying the server's lifecycle preamble on top of the frames the first stream delivered.
        SelectedApprentice = detail;

        await detail.LoadAsync(cancellationToken).ConfigureAwait(true);

    }

    public async Task<bool> SelectApprenticeByIdAsync(
        Guid apprenticeId,
        CancellationToken cancellationToken)
    {

        LastError = null;

        try
        {

            ApprenticeDetailDto? apprentice = await _dataSource
                .GetApprenticeAsync(apprenticeId, cancellationToken)
                .ConfigureAwait(true);

            if (apprentice is null || apprentice.Id != apprenticeId)
            {

                LastError = "The requested apprentice is unavailable.";

                return false;

            }

            ApprenticeDetailViewModel detail = new(apprenticeId, _dataSource);

            await detail
                .LoadKnownDetailAsync(apprentice, cancellationToken)
                .ConfigureAwait(true);

            SelectedApprentice = detail;

            return true;

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            return false;

        }

    }

    [RelayCommand]
    public async Task OpenCreatePanelAsync(CancellationToken cancellationToken)
    {

        IsCreatePanelOpen = true;

        try
        {

            IReadOnlyList<CampaignDto> campaigns = await _dataSource.ListCampaignsAsync(cancellationToken).ConfigureAwait(true);

            Campaigns.Clear();

            foreach (CampaignDto campaign in campaigns)
            {

                Campaigns.Add(campaign);

            }

            IReadOnlyList<WorkspaceInfo> workspaces = await _dataSource.ListWorkspacesAsync(cancellationToken).ConfigureAwait(true);

            Workspaces.Clear();

            foreach (WorkspaceInfo workspace in workspaces)
            {

                Workspaces.Add(workspace);

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }

    }

    [RelayCommand]
    private void CloseCreatePanel()
    {

        IsCreatePanelOpen = false;

    }

    [RelayCommand]
    public async Task CreateApprenticeAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewGoal))
        {

            LastError = "Name and goal are required.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            CreateApprenticeRequest request = new(
                NewName.Trim(),
                NewGoal.Trim(),
                SelectedCampaign?.Id,
                SelectedWorkspace?.Path);

            ApprenticeDetailDto? created = await _dataSource.CreateApprenticeAsync(request, cancellationToken).ConfigureAwait(true);

            if (created is null)
            {

                LastError = "Failed to create apprentice.";

                return;

            }

            IsCreatePanelOpen = false;

            NewName = string.Empty;

            NewGoal = string.Empty;

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

            await SelectApprenticeAsync(
                new ApprenticeSummaryDto(
                    created.Id,
                    created.CampaignId,
                    created.Name,
                    created.Goal,
                    created.Status,
                    created.CurrentStep,
                    created.Plan.Count,
                    created.CreatedAt,
                    created.UpdatedAt),
                cancellationToken).ConfigureAwait(true);

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsBusy = false;

        }

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        SelectedApprentice?.Dispose();

        SelectedApprentice = null;

        GC.SuppressFinalize(this);

    }

}
