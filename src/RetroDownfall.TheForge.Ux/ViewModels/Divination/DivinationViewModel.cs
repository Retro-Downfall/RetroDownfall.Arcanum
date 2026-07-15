using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

namespace RetroDownfall.TheForge.Ux.ViewModels.Divination;

/// <summary>
/// Divination — semantic search over sessions, workspace files, and Saga memories. A tabbed dock tool;
/// each tab calls one verified Arcanum Divination route through <see cref="IDivinationDataSource"/>. Every
/// route is server-gated on Embeddings (+ the relevant sub-flag) and surfaces
/// <c>Embeddings.FeatureDisabled</c> as an honest per-tab disabled state. Session results with a
/// <c>SessionId</c> open The Tome; workspace/Saga results show detail in-place (focusing their panels is
/// wired in the shell-integration phase). Nothing throws on API failure; The Forge never embeds or
/// searches client-side.
/// </summary>
public sealed partial class DivinationViewModel : ViewModelBase
{

    private readonly IDivinationDataSource _dataSource;

    private readonly IWorkspaceExplorerDataSource _workspaceDataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IClipboardService _clipboard;

    private bool _loaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isVisible;

    // Sessions tab
    [ObservableProperty]
    private string _sessionQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchingSessions;

    [ObservableProperty]
    private bool _sessionsFeatureDisabled;

    // Workspace Files tab
    [ObservableProperty]
    private WorkspaceInfo? _selectedWorkspace;

    [ObservableProperty]
    private string _workspaceQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchingWorkspace;

    [ObservableProperty]
    private bool _workspaceFeatureDisabled;

    // Saga tab
    [ObservableProperty]
    private string _sagaQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchingSaga;

    [ObservableProperty]
    private bool _sagaFeatureDisabled;

    public DivinationViewModel(
        IDivinationDataSource dataSource,
        IWorkspaceExplorerDataSource workspaceDataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IClipboardService clipboard)
    {

        _dataSource = dataSource;

        _workspaceDataSource = workspaceDataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _clipboard = clipboard;

        Title = "Divination";

    }

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    public ObservableCollection<SemanticSessionSearchResult> SessionResults { get; } = [];

    public ObservableCollection<WorkspaceSearchResult> WorkspaceResults { get; } = [];

    public ObservableCollection<SagaMemoryDto> SagaResults { get; } = [];

    public string SessionsFeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Session Divination", DisabledSettingPaths.SessionDivination);

    public string WorkspaceFeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Workspace Divination", DisabledSettingPaths.WorkspaceDivination);

    public string SagaFeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Saga Divination", DisabledSettingPaths.SagaDivination);

    [RelayCommand]
    private async Task CopyDisabledPathsAsync(string? surface, CancellationToken cancellationToken)
    {

        string[] paths = surface switch
        {

            "SessionDivination" => DisabledSettingPaths.SessionDivination,

            "WorkspaceDivination" => DisabledSettingPaths.WorkspaceDivination,

            "SagaDivination" => DisabledSettingPaths.SagaDivination,

            _ => [],

        };

        if (paths.Length == 0)
        {

            return;

        }

        await _clipboard.SetTextAsync(DisabledSettingPaths.JoinForClipboard(paths), cancellationToken).ConfigureAwait(true);

    }

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_loaded)
        {

            _loaded = true;

            _ = RefreshAsync(CancellationToken.None);

        }

    }

    /// <summary>Loads the workspace list for the Workspace Files tab selector.</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<WorkspaceInfo[]> result = await _workspaceDataSource
                .ListWorkspacesAsync(cancellationToken)
                .ConfigureAwait(true);

            Workspaces.Clear();

            if (result.Success && result.Data is { } workspaces)
            {

                foreach (WorkspaceInfo workspace in workspaces)
                {

                    Workspaces.Add(workspace);

                }

                StatusText = "Divination ready.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load workspaces.";

                StatusText = "Divination unavailable.";

                _foundryFloor.AppendLine($"Divination load failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Divination refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task SearchSessionsAsync(CancellationToken cancellationToken)
    {

        string query = SessionQuery.Trim();

        if (string.IsNullOrEmpty(query))
        {

            StatusText = "Enter a session query.";

            return;

        }

        IsSearchingSessions = true;

        LastError = null;

        SessionsFeatureDisabled = false;

        try
        {

            DataSourceResult<SemanticSearchResult> result = await _dataSource
                .DivineSessionsAsync(new SemanticSearchRequest(query), cancellationToken)
                .ConfigureAwait(true);

            SessionResults.Clear();

            if (result.Success && result.Data is { } search)
            {

                foreach (SemanticSessionSearchResult hit in search.Results)
                {

                    SessionResults.Add(hit);

                }

                StatusText = SessionResults.Count == 0 ? "No session results." : $"{SessionResults.Count} session results.";

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                SessionsFeatureDisabled = true;

                StatusText = "Session Divination disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Session Divination failed.";

                StatusText = "Session Divination failed.";

                _foundryFloor.AppendLine($"Divination sessions failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsSearchingSessions = false;

        }

    }

    [RelayCommand]
    public async Task SearchWorkspaceAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        string query = WorkspaceQuery.Trim();

        if (string.IsNullOrEmpty(query))
        {

            StatusText = "Enter a workspace query.";

            return;

        }

        IsSearchingWorkspace = true;

        LastError = null;

        WorkspaceFeatureDisabled = false;

        try
        {

            DataSourceResult<WorkspaceSearchResult[]> result = await _dataSource
                .DivineWorkspaceFilesAsync(workspace.Id, new WorkspaceSemanticSearchRequest(query), cancellationToken)
                .ConfigureAwait(true);

            WorkspaceResults.Clear();

            if (result.Success && result.Data is { } results)
            {

                foreach (WorkspaceSearchResult hit in results)
                {

                    WorkspaceResults.Add(hit);

                }

                StatusText = WorkspaceResults.Count == 0 ? "No workspace results." : $"{WorkspaceResults.Count} workspace results.";

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                WorkspaceFeatureDisabled = true;

                StatusText = "Workspace Divination disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Workspace Divination failed.";

                StatusText = "Workspace Divination failed.";

                _foundryFloor.AppendLine($"Divination workspace failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsSearchingWorkspace = false;

        }

    }

    [RelayCommand]
    public async Task SearchSagaAsync(CancellationToken cancellationToken)
    {

        string query = SagaQuery.Trim();

        if (string.IsNullOrEmpty(query))
        {

            StatusText = "Enter a Saga query.";

            return;

        }

        IsSearchingSaga = true;

        LastError = null;

        SagaFeatureDisabled = false;

        try
        {

            DataSourceResult<SagaSearchResult> result = await _dataSource
                .DivineSagaAsync(new SagaSearchRequest(query), cancellationToken)
                .ConfigureAwait(true);

            SagaResults.Clear();

            if (result.Success && result.Data is { } search)
            {

                foreach (SagaMemoryDto memory in search.Memories)
                {

                    SagaResults.Add(memory);

                }

                StatusText = SagaResults.Count == 0 ? "No Saga results." : $"{SagaResults.Count} Saga results.";

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                SagaFeatureDisabled = true;

                StatusText = "Saga Divination disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Saga Divination failed.";

                StatusText = "Saga Divination failed.";

                _foundryFloor.AppendLine($"Divination saga failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsSearchingSaga = false;

        }

    }

    /// <summary>Opens The Tome for the session a Divination hit belongs to (when <c>SessionId</c> is present).</summary>
    [RelayCommand]
    private void OpenSessionResult(SemanticSessionSearchResult? result)
    {

        if (result is { } hit)
        {

            _navigation.OpenDocument(DocumentKind.Session, hit.SessionId.ToString("D"));

        }

    }

}
