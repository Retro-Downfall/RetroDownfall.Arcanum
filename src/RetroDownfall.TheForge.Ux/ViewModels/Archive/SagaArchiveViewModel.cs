using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Archive;

/// <summary>
/// The Archive — Saga long-term associative memory over <c>/api/saga/*</c>. A dock tool that lists
/// memories, shows stats, runs Saga Divination (semantic search), deletes a single memory, and
/// offers a guarded delete-all. List, stats, and delete are always available; Divination is
/// server-gated on Embeddings+SagaEnabled and surfaces <c>Embeddings.FeatureDisabled</c> as an honest
/// disabled state. Nothing throws on API failure.
/// </summary>
public sealed partial class SagaArchiveViewModel : ViewModelBase
{

    private readonly ISagaArchiveDataSource _dataSource;

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

    public string ManagedWeaveBannerMessage => ManagedWeaveBanner.Message;

    [ObservableProperty]
    private SagaStats? _stats;

    [ObservableProperty]
    private SagaMemoryDto? _selectedMemory;

    [ObservableProperty]
    private string _divinationQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isFeatureDisabled;

    [ObservableProperty]
    private bool _isSearchActive;

    public SagaArchiveViewModel(
        ISagaArchiveDataSource dataSource,
        FoundryFloorViewModel foundryFloor,
        IConfirmationDialogService confirmationDialog,
        IClipboardService clipboard,
        IWhispersService whispers,
        IArcanumConnection connection)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        _confirmationDialog = confirmationDialog;

        _clipboard = clipboard;

        _whispers = whispers;

        _connection = connection;

        _connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IArcanumConnection.LastMeta) or nameof(IArcanumConnection.State))
            {
                ShowManagedWeaveBanner = ManagedWeaveBanner.ShouldShow(_connection.LastMeta);
            }
        };

        ShowManagedWeaveBanner = ManagedWeaveBanner.ShouldShow(_connection.LastMeta);

        Title = "The Archive";

    }

    public ObservableCollection<SagaMemoryDto> Memories { get; } = [];

    public bool HasNoMemories => Memories.Count == 0;

    public string EmptyState => "No Saga memories yet.";

    public string FeatureDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Saga Divination", DisabledSettingPaths.SagaDivination);

    [RelayCommand]
    private async Task CopyDisabledPathsAsync(CancellationToken cancellationToken) =>
        await _clipboard
            .SetTextAsync(DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.SagaDivination), cancellationToken)
            .ConfigureAwait(true);

    public string StatsText => Stats is { } s ? $"{s.TotalCount} memories across {s.SessionCount} sessions" : string.Empty;

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_loaded)
        {

            _loaded = true;

            _ = RefreshAsync(CancellationToken.None);

        }

    }

    partial void OnStatsChanged(SagaStats? value)
    {

        OnPropertyChanged(nameof(StatsText));

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        IsSearchActive = false;

        try
        {

            DataSourceResult<SagaMemoryDto[]> listResult = await _dataSource
                .ListAsync(null, null, null, null, cancellationToken)
                .ConfigureAwait(true);

            DataSourceResult<SagaStats> statsResult = await _dataSource
                .GetStatsAsync(cancellationToken)
                .ConfigureAwait(true);

            Memories.Clear();

            if (listResult.Success && listResult.Data is { } memories)
            {

                foreach (SagaMemoryDto memory in memories)
                {

                    Memories.Add(memory);

                }

                StatusText = Memories.Count == 0 ? "No Saga memories yet." : $"{Memories.Count} Saga memories.";

            }

            else
            {

                LastError = listResult.ErrorMessage ?? "Failed to load Saga memories.";

                StatusText = "Saga unavailable.";

                _foundryFloor.AppendLine($"Archive load failed: {LastError}");

            }

            if (statsResult.Success && statsResult.Data is not null)
            {

                Stats = statsResult.Data;

            }

            OnPropertyChanged(nameof(HasNoMemories));

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Archive refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DivineAsync(CancellationToken cancellationToken)
    {

        string query = DivinationQuery.Trim();

        if (string.IsNullOrEmpty(query))
        {

            StatusText = "Enter a Divination query.";

            return;

        }

        IsSearching = true;

        LastError = null;

        IsFeatureDisabled = false;

        try
        {

            DataSourceResult<SagaSearchResult> result = await _dataSource
                .DivineAsync(query, null, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } search)
            {

                Memories.Clear();

                foreach (SagaMemoryDto memory in search.Memories)
                {

                    Memories.Add(memory);

                }

                IsSearchActive = true;

                StatusText = Memories.Count == 0 ? "No results." : $"{Memories.Count} results.";

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                IsFeatureDisabled = true;

                StatusText = "Divination disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Divination failed.";

                StatusText = "Divination failed.";

                _foundryFloor.AppendLine($"Archive divination failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoMemories));

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsSearching = false;

        }

    }

    [RelayCommand]
    private async Task ClearSearchAsync(CancellationToken cancellationToken)
    {

        DivinationQuery = string.Empty;

        await RefreshAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task DeleteMemoryAsync(CancellationToken cancellationToken)
    {

        if (SelectedMemory is not { } selected)
        {

            StatusText = "Select a memory to delete.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<bool> result = await _dataSource
                .DeleteAsync(selected.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "Memory deleted.";

                SelectedMemory = null;

                _whispers.Show(WhisperSeverity.Success, "Memory deleted.");

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to delete memory.";

                StatusText = "Delete failed.";

                _foundryFloor.AppendLine($"Archive delete failed: {LastError}");

                _whispers.Show(WhisperSeverity.Error, "Delete failed.");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Delete failed.");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DeleteAllMemoriesAsync(CancellationToken cancellationToken)
    {

        bool confirmed = await _confirmationDialog
            .ConfirmAsync(
                "Delete all Saga memories",
                "This permanently deletes every Saga memory. This cannot be undone. Continue?",
                cancellationToken)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<bool> result = await _dataSource
                .DeleteAllAsync(cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "All Saga memories deleted.";

                SelectedMemory = null;

                _foundryFloor.AppendLine("Archive: deleted all Saga memories.");

                _whispers.Show(WhisperSeverity.Success, "All memories deleted.");

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to delete all Saga memories.";

                StatusText = "Delete all failed.";

                _foundryFloor.AppendLine($"Archive delete-all failed: {LastError}");

                _whispers.Show(WhisperSeverity.Error, "Delete all failed.");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Archive delete-all error: {ex.Message}");

            _whispers.Show(WhisperSeverity.Error, "Delete all failed.");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RefreshStatsAsync(CancellationToken cancellationToken)
    {

        try
        {

            DataSourceResult<SagaStats> result = await _dataSource
                .GetStatsAsync(cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is not null)
            {

                Stats = result.Data;

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

    }

}
