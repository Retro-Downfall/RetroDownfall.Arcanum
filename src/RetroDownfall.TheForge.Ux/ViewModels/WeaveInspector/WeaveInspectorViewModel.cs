using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.Archive;
using RetroDownfall.TheForge.Ux.ViewModels.Divination;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

namespace RetroDownfall.TheForge.Ux.ViewModels.WeaveInspector;

/// <summary>
/// The Weave Inspector (Phase 7) — a tabbed dock tool for inspecting the RAG retrieval substrate, not just
/// running semantic searches. Surfaces the read-only Arcanum <c>GET /api/workspaces/{id}/files/index/status</c>
/// and <c>GET /api/workspaces/{id}/files/chunks</c> routes through <see cref="IWeaveInspectorDataSource"/>,
/// plus the existing manual re-index (<c>POST /api/workspaces/{id}/files/index</c>) and the destructive
/// embeddings reset (<c>POST /api/embeddings/reset?scope=workspace_file&amp;confirm=true</c>, strong
/// confirmation always). Vector mode comes from the live <c>/api/meta</c> payload on
/// <see cref="IArcanumConnection.LastMeta"/>. The Workspace Divination / Saga / Sessions tabs reuse the
/// existing data sources; Saga similarities are displayed here (the Divination tool's Saga tab drops them).
/// Skipped-file reasons are surfaced honestly from the status payload — never synthesized. Nothing throws
/// on API failure; nothing embeds or searches client-side.
/// </summary>
public sealed partial class WeaveInspectorViewModel : ViewModelBase
{

    /// <summary>Default <c>workspace_file</c> scope — the narrowest relevant reset for the workspace index.</summary>
    public const string DefaultResetScope = "workspace_file";

    public const int ChunkLimit = 50;

    public const string ResetWarning =
        "Resetting embeddings is destructive and cannot be undone. This deletes the workspace's indexed " +
        "chunks and embeddings for the chosen scope. Re-index to rebuild.";

    private readonly IWeaveInspectorDataSource _inspectorDataSource;

    private readonly IWorkspaceExplorerDataSource _workspaceDataSource;

    private readonly IDivinationDataSource _divinationDataSource;

    private readonly ISagaArchiveDataSource _sagaDataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IConfirmationDialogService _confirmationDialog;

    private readonly IClipboardService _clipboard;

    private readonly IWhispersService _whispers;

    private readonly IArcanumConnection _connection;

    private bool _loaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _showManagedWeaveBanner;

    [ObservableProperty]
    private int _activeTabIndex;

    // Index tab
    [ObservableProperty]
    private WorkspaceInfo? _selectedWorkspace;

    [ObservableProperty]
    private WorkspaceIndexStatusDto? _status;

    [ObservableProperty]
    private string _chunkFilterRelativePath = string.Empty;

    [ObservableProperty]
    private int _chunkOffset;

    [ObservableProperty]
    private int _chunkTotal;

    [ObservableProperty]
    private bool _chunkHasMore;

    [ObservableProperty]
    private WorkspaceFileChunkDto? _selectedChunk;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private bool _isResetting;

    // Workspace Divination tab
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

    [ObservableProperty]
    private SagaStats? _sagaStats;

    // Sessions tab
    [ObservableProperty]
    private string _sessionQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearchingSessions;

    [ObservableProperty]
    private bool _sessionsFeatureDisabled;

    public WeaveInspectorViewModel(
        IWeaveInspectorDataSource inspectorDataSource,
        IWorkspaceExplorerDataSource workspaceDataSource,
        IDivinationDataSource divinationDataSource,
        ISagaArchiveDataSource sagaDataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IConfirmationDialogService confirmationDialog,
        IClipboardService clipboard,
        IWhispersService whispers,
        IArcanumConnection connection)
    {

        _inspectorDataSource = inspectorDataSource;

        _workspaceDataSource = workspaceDataSource;

        _divinationDataSource = divinationDataSource;

        _sagaDataSource = sagaDataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _confirmationDialog = confirmationDialog;

        _clipboard = clipboard;

        _whispers = whispers;

        _connection = connection;

        _connection.PropertyChanged += OnConnectionPropertyChanged;

        RefreshManagedWeaveBanner();

        Title = "The Weave Inspector";

    }

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    public ObservableCollection<WorkspaceFileChunkDto> Chunks { get; } = [];

    public ObservableCollection<WorkspaceSearchResult> WorkspaceResults { get; } = [];

    public ObservableCollection<SagaMemoryWithSimilarity> SagaResults { get; } = [];

    public ObservableCollection<SemanticSessionSearchResult> SessionResults { get; } = [];

    public string ManagedWeaveBannerMessage => ManagedWeaveBanner.Message;

    public string VectorModeText => _connection.LastMeta?.EmbeddingsVectorMode ?? "unknown";

    public string VectorDiagnosticText => _connection.LastMeta?.EmbeddingsVectorDiagnostic ?? string.Empty;

    public bool EmbeddingsEnabled => _connection.LastMeta?.EmbeddingsEnabled ?? false;

    public bool IndexingEnabled => Status?.IndexingEnabled ?? false;

    public string IndexingDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Workspace indexing", DisabledSettingPaths.WorkspaceIndexing);

    public string WorkspaceFeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Workspace Divination", DisabledSettingPaths.WorkspaceDivination);

    public string SagaFeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Saga Divination", DisabledSettingPaths.SagaDivination);

    public string SessionsFeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Session Divination", DisabledSettingPaths.SessionDivination);

    public string SkippedFilesNote => Status?.SkippedFilesNote ?? string.Empty;

    public string SagaStatsText => SagaStats is { } s
        ? $"{s.TotalCount} memories across {s.SessionCount} sessions"
        : string.Empty;

    public string StatusSummaryText
    {

        get
        {

            if (Status is not { } status)
            {

                return string.Empty;

            }

            return $"{status.TotalIndexedFiles} file(s), {status.TotalChunks} chunk(s)" +
                (status.EmbeddingsDimensions is { } dim ? $", {dim}-dim" : string.Empty) +
                (status.NewestIndexedAt is { } newest ? $", last indexed {newest:u}" : string.Empty);

        }

    }

    public string ChunkRangeText => Chunks.Count == 0
        ? (ChunkTotal == 0 ? "No chunks." : "No chunks on this page.")
        : $"Showing {ChunkOffset + 1}–{ChunkOffset + Chunks.Count} of {ChunkTotal}";

    public bool CanGoPreviousChunkPage => ChunkOffset > 0 && !IsBusy;

    public bool CanGoNextChunkPage => ChunkHasMore && !IsBusy;

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(IArcanumConnection.LastMeta) or nameof(IArcanumConnection.State))
        {

            RefreshManagedWeaveBanner();

            OnPropertyChanged(nameof(VectorModeText));

            OnPropertyChanged(nameof(VectorDiagnosticText));

            OnPropertyChanged(nameof(EmbeddingsEnabled));

        }

    }

    private void RefreshManagedWeaveBanner() =>
        ShowManagedWeaveBanner = ManagedWeaveBanner.ShouldShow(_connection.LastMeta);

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_loaded)
        {

            _loaded = true;

            _ = RefreshAsync(CancellationToken.None);

        }

    }

    partial void OnStatusChanged(WorkspaceIndexStatusDto? value)
    {

        OnPropertyChanged(nameof(IndexingEnabled));

        OnPropertyChanged(nameof(StatusSummaryText));

        OnPropertyChanged(nameof(SkippedFilesNote));

    }

    partial void OnSagaStatsChanged(SagaStats? value) => OnPropertyChanged(nameof(SagaStatsText));

    partial void OnSelectedWorkspaceChanged(WorkspaceInfo? value)
    {

        Status = null;

        Chunks.Clear();

        ChunkTotal = 0;

        ChunkHasMore = false;

        ChunkOffset = 0;

        SelectedChunk = null;

        if (value is not null && _isVisible)
        {

            _ = LoadStatusAsync(CancellationToken.None);

            _ = LoadChunksAsync(CancellationToken.None);

        }

    }

    partial void OnIsBusyChanged(bool value)
    {

        OnPropertyChanged(nameof(CanGoPreviousChunkPage));

        OnPropertyChanged(nameof(CanGoNextChunkPage));

    }

    partial void OnChunkOffsetChanged(int value)
    {

        OnPropertyChanged(nameof(CanGoPreviousChunkPage));

        OnPropertyChanged(nameof(CanGoNextChunkPage));

    }

    partial void OnChunkHasMoreChanged(bool value)
    {

        OnPropertyChanged(nameof(CanGoNextChunkPage));

    }

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

                if (SelectedWorkspace is null && Workspaces.Count > 0)
                {

                    SelectedWorkspace = Workspaces[0];

                }

                StatusText = Workspaces.Count == 0 ? "No workspaces registered." : "The Weave Inspector ready.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load workspaces.";

                StatusText = "Inspector unavailable.";

                _foundryFloor.AppendLine($"Weave Inspector load failed: {LastError}");

            }

            if (SelectedWorkspace is not null)
            {

                await LoadStatusAsync(cancellationToken).ConfigureAwait(true);

                await LoadChunksAsync(cancellationToken).ConfigureAwait(true);

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Weave Inspector refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task LoadStatusAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        try
        {

            DataSourceResult<WorkspaceIndexStatusDto> result = await _inspectorDataSource
                .GetIndexStatusAsync(workspace.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } status)
            {

                Status = status;

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load index status.";

                Status = null;

                _foundryFloor.AppendLine($"Weave Inspector status failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

    }

    [RelayCommand]
    public async Task LoadChunksAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            string? filter = string.IsNullOrWhiteSpace(ChunkFilterRelativePath) ? null : ChunkFilterRelativePath.Trim();

            DataSourceResult<WorkspaceFileChunkPage> result = await _inspectorDataSource
                .GetChunksAsync(workspace.Id, filter, ChunkLimit, ChunkOffset, cancellationToken)
                .ConfigureAwait(true);

            Chunks.Clear();

            SelectedChunk = null;

            if (result.Success && result.Data is { } page)
            {

                foreach (WorkspaceFileChunkDto chunk in page.Chunks)
                {

                    Chunks.Add(chunk);

                }

                ChunkTotal = page.Total;

                ChunkHasMore = page.HasMore;

                StatusText = Chunks.Count == 0 ? "No indexed chunks." : ChunkRangeText;

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                // Status route is not feature-gated, but be honest if the server reports it.
                StatusText = "Workspace indexing is disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load chunks.";

                StatusText = "Chunks unavailable.";

                _foundryFloor.AppendLine($"Weave Inspector chunks failed: {LastError}");

            }

            OnPropertyChanged(nameof(ChunkRangeText));

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
    public async Task NextChunkPageAsync(CancellationToken cancellationToken)
    {

        if (!CanGoNextChunkPage)
        {

            return;

        }

        ChunkOffset += ChunkLimit;

        await LoadChunksAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task PreviousChunkPageAsync(CancellationToken cancellationToken)
    {

        if (!CanGoPreviousChunkPage)
        {

            return;

        }

        ChunkOffset = Math.Max(0, ChunkOffset - ChunkLimit);

        await LoadChunksAsync(cancellationToken).ConfigureAwait(true);

    }

    /// <summary>Cross-tab link from a Workspace Divination hit: filter the chunk browser to that file and focus the Index tab.</summary>
    [RelayCommand]
    public async Task LoadChunksForFileAsync(string? relativePath)
    {

        if (string.IsNullOrWhiteSpace(relativePath))
        {

            return;

        }

        ChunkFilterRelativePath = relativePath;

        ChunkOffset = 0;

        ActiveTabIndex = 0;

        await LoadChunksAsync(CancellationToken.None).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task ReindexAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        IsIndexing = true;

        LastError = null;

        try
        {

            DataSourceResult<bool> result = await _workspaceDataSource
                .IndexWorkspaceAsync(workspace.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "Re-index triggered (runs in the background). Refresh status shortly.";

                _foundryFloor.AppendLine($"Weave Inspector: re-index triggered for {workspace.Name}.");

                _whispers.Show(WhisperSeverity.Success, "Re-index triggered.");

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                WorkspaceFeatureDisabled = true;

                StatusText = "Workspace indexing is disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to trigger re-index.";

                StatusText = "Re-index failed.";

                _whispers.Show(WhisperSeverity.Error, "Re-index failed.");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsIndexing = false;

        }

    }

    [RelayCommand]
    public async Task ResetEmbeddingsAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Reset workspace embeddings",
                $"{ResetWarning}\n\nWorkspace: {workspace.Name}\nScope: {DefaultResetScope}",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            StatusText = "Reset cancelled.";

            return;

        }

        IsResetting = true;

        LastError = null;

        try
        {

            DataSourceResult<EmbeddingsResetResult> result = await _inspectorDataSource
                .ResetEmbeddingsAsync(DefaultResetScope, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } reset)
            {

                string deleted = reset.DeletedRowCounts is { } counts
                    ? string.Join(", ", counts.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => $"{kv.Key}={kv.Value}"))
                    : "(no rows)";

                _foundryFloor.AppendLine($"Weave Inspector: embeddings reset ({DefaultResetScope}) for {workspace.Name}. Deleted: {deleted}");

                _whispers.Show(WhisperSeverity.Success, "Embeddings reset.");

                // The substrate just changed — refresh the inspector view.
                await LoadStatusAsync(cancellationToken).ConfigureAwait(true);

                ChunkOffset = 0;

                await LoadChunksAsync(cancellationToken).ConfigureAwait(true);

                // Re-assert last so the reload's chunk-range status doesn't clobber the destructive-action confirmation.
                StatusText = $"Reset complete. Deleted: {deleted}.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to reset embeddings.";

                StatusText = "Reset failed.";

                _whispers.Show(WhisperSeverity.Error, "Reset failed.");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Reset failed.");

        }

        finally
        {

            IsResetting = false;

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

            DataSourceResult<WorkspaceSearchResult[]> result = await _workspaceDataSource
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

                _foundryFloor.AppendLine($"Weave Inspector workspace divination failed: {LastError}");

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

            DataSourceResult<SagaSearchResult> result = await _sagaDataSource
                .DivineAsync(query, null, cancellationToken)
                .ConfigureAwait(true);

            SagaResults.Clear();

            if (result.Success && result.Data is { } search)
            {

                for (int i = 0; i < search.Memories.Length; i++)
                {

                    float similarity = i < search.Similarities.Length ? search.Similarities[i] : 0f;

                    SagaResults.Add(new SagaMemoryWithSimilarity(search.Memories[i], similarity));

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

                _foundryFloor.AppendLine($"Weave Inspector saga divination failed: {LastError}");

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

            DataSourceResult<SemanticSearchResult> result = await _divinationDataSource
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

                _foundryFloor.AppendLine($"Weave Inspector session divination failed: {LastError}");

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

    /// <summary>Loads Saga stats for the Saga tab (not gated on SagaEnabled).</summary>
    [RelayCommand]
    public async Task RefreshSagaStatsAsync(CancellationToken cancellationToken)
    {

        try
        {

            DataSourceResult<SagaStats> result = await _sagaDataSource.GetStatsAsync(cancellationToken).ConfigureAwait(true);

            if (result.Success && result.Data is not null)
            {

                SagaStats = result.Data;

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

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

    [RelayCommand]
    private async Task CopyDisabledPathsAsync(string? surface, CancellationToken cancellationToken)
    {

        string[] paths = surface switch
        {

            "WorkspaceIndexing" => DisabledSettingPaths.WorkspaceIndexing,

            "WorkspaceDivination" => DisabledSettingPaths.WorkspaceDivination,

            "SagaDivination" => DisabledSettingPaths.SagaDivination,

            "SessionDivination" => DisabledSettingPaths.SessionDivination,

            _ => [],

        };

        if (paths.Length == 0)
        {

            return;

        }

        await _clipboard.SetTextAsync(DisabledSettingPaths.JoinForClipboard(paths), cancellationToken).ConfigureAwait(true);

    }

}

/// <summary>A Saga memory paired with its Divination similarity, for the inspector's Saga tab display.</summary>
public sealed record SagaMemoryWithSimilarity(SagaMemoryDto Memory, float Similarity);
