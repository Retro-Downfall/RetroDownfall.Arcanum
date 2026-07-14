using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Archive;

/// <summary>
/// The Archive — Saga long-term associative memory over <c>/api/saga/*</c>. A dock tool that lists
/// memories, shows stats, runs Saga Divination (semantic search), and deletes a single memory. List,
/// stats, and delete are always available; Divination is server-gated on Embeddings+SagaEnabled and
/// surfaces <c>Embeddings.FeatureDisabled</c> as an honest disabled state. Nothing throws on API failure.
/// </summary>
public sealed partial class SagaArchiveViewModel : ViewModelBase
{

    private readonly ISagaArchiveDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

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

    public SagaArchiveViewModel(ISagaArchiveDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "The Archive";

    }

    public ObservableCollection<SagaMemoryDto> Memories { get; } = [];

    public bool HasNoMemories => Memories.Count == 0;

    public string EmptyState => "No Saga memories yet.";

    public string FeatureDisabledMessage =>
        "Saga Divination is disabled — enable Arcanum:Embeddings:Enabled and Arcanum:Embeddings:SagaEnabled server-side.";

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

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to delete memory.";

                StatusText = "Delete failed.";

                _foundryFloor.AppendLine($"Archive delete failed: {LastError}");

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
