using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Lore;

/// <summary>
/// The Lore Browser — operator key-value Lore over <c>/api/lore/*</c> (list/get/upsert/delete). A dock
/// tool that refreshes on first show. Selecting an entry loads its key/value into the editor; Save
/// upserts and refreshes; Delete removes the selected entry. API failures surface inline via
/// <see cref="LastError"/> and the Foundry Floor — nothing throws.
/// </summary>
public sealed partial class LoreBrowserViewModel : ViewModelBase
{

    private readonly ILoreDataSource _dataSource;

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
    private LoreDto? _selectedLore;

    [ObservableProperty]
    private string _editKey = string.Empty;

    [ObservableProperty]
    private string _editValue = string.Empty;

    public LoreBrowserViewModel(ILoreDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "Lore Browser";

    }

    public ObservableCollection<LoreDto> Lore { get; } = [];

    public bool HasNoLore => Lore.Count == 0;

    public string EmptyState => "No Lore entries yet.";

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_loaded)
        {

            _loaded = true;

            _ = RefreshAsync(CancellationToken.None);

        }

    }

    partial void OnSelectedLoreChanged(LoreDto? value)
    {

        if (value is { } lore)
        {

            EditKey = lore.Key;

            EditValue = lore.Value;

        }

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<ListPageResult<LoreDto>> result = await _dataSource
                .ListAsync(cancellationToken)
                .ConfigureAwait(true);

            Lore.Clear();

            if (result.Success && result.Data is { } page)
            {

                foreach (LoreDto lore in page.Items)
                {

                    Lore.Add(lore);

                }

                StatusText = Lore.Count == 0 ? "No Lore entries yet." : $"{Lore.Count} Lore entries.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load Lore.";

                StatusText = "Lore unavailable.";

                _foundryFloor.AppendLine($"Lore Browser load failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoLore));

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Lore refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    private void ClearEditor()
    {

        SelectedLore = null;

        EditKey = string.Empty;

        EditValue = string.Empty;

        StatusText = "New Lore entry.";

    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken)
    {

        string key = EditKey.Trim();

        if (string.IsNullOrEmpty(key))
        {

            LastError = "Key is required.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<LoreDto> result = await _dataSource
                .UpsertAsync(key, EditValue, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is not null)
            {

                StatusText = "Lore saved.";

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to save Lore.";

                StatusText = "Save failed.";

                _foundryFloor.AppendLine($"Lore save failed: {LastError}");

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
    public async Task DeleteAsync(CancellationToken cancellationToken)
    {

        if (SelectedLore is not { } selected)
        {

            StatusText = "Select a Lore entry to delete.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<bool> result = await _dataSource
                .DeleteAsync(selected.Key, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "Lore deleted.";

                SelectedLore = null;

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to delete Lore.";

                StatusText = "Delete failed.";

                _foundryFloor.AppendLine($"Lore delete failed: {LastError}");

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

}
