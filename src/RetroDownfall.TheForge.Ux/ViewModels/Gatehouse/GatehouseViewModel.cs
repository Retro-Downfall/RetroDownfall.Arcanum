using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Wards;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.ViewModels.Gatehouse;

/// <summary>
/// The Gatehouse polls pending wards every 2s while visible, renders countdown cards, and
/// resolves approve/deny via <c>POST /api/wards/{id}</c>. Ward SSE auto-refresh is a future hook.
/// </summary>
public sealed partial class GatehouseViewModel : ViewModelBase, IDisposable
{

    private readonly IGatehouseDataSource _dataSource;

    private readonly IWhispersService _whispers;

    private CancellationTokenSource? _pollCts;

    private bool _disposed;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private string? _lastError;

    public GatehouseViewModel(IGatehouseDataSource dataSource, IWhispersService whispers)
    {

        _dataSource = dataSource;

        _whispers = whispers;

        Title = "The Gatehouse";

    }

    public ObservableCollection<WardCardViewModel> Wards { get; } = [];

    public bool HasNoWards => Wards.Count == 0;

    public string EmptyState => "No active wards — The Forge is quiet.";

    partial void OnIsVisibleChanged(bool value)
    {

        if (value)
        {

            StartPolling();

        }
        else
        {

            StopPolling();

        }

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsRefreshing = true;

        LastError = null;

        try
        {

            IReadOnlyList<WardDto> wards = await _dataSource.ListWardsAsync(cancellationToken).ConfigureAwait(true);

            SyncWards(wards);

            RefreshCountdowns(DateTimeOffset.UtcNow);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Expected when visibility drops mid-poll.

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

        }
        finally
        {

            IsRefreshing = false;

        }

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        StopPolling();

        GC.SuppressFinalize(this);

    }

    private void StartPolling()
    {

        StopPolling();

        _pollCts = new CancellationTokenSource();

        CancellationToken token = _pollCts.Token;

        _ = PollLoopAsync(token);

    }

    private void StopPolling()
    {

        if (_pollCts is null)
        {

            return;

        }

        _pollCts.Cancel();

        _pollCts.Dispose();

        _pollCts = null;

    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {

        try
        {

            while (!cancellationToken.IsCancellationRequested)
            {

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(true);

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Visibility gated stop.

        }

    }

    private void SyncWards(IReadOnlyList<WardDto> wards)
    {

        Dictionary<string, WardDto> byId = wards.ToDictionary(static ward => ward.WardId, StringComparer.Ordinal);

        for (int index = Wards.Count - 1; index >= 0; index--)
        {

            if (!byId.ContainsKey(Wards[index].WardId))
            {

                Wards.RemoveAt(index);

            }

        }

        Dictionary<string, WardCardViewModel> cardsById = Wards.ToDictionary(
            static card => card.WardId,
            StringComparer.Ordinal);

        foreach (WardDto ward in wards)
        {

            if (cardsById.TryGetValue(ward.WardId, out WardCardViewModel? existingCard))
            {

                existingCard.Update(ward);

                continue;

            }

            Wards.Add(new WardCardViewModel(ward, ResolveWardAsync));

        }

        OnPropertyChanged(nameof(HasNoWards));

    }

    private void RefreshCountdowns(DateTimeOffset utcNow)
    {

        foreach (WardCardViewModel card in Wards)
        {

            card.RefreshCountdown(utcNow);

        }

    }

    private async Task ResolveWardAsync(WardCardViewModel card, bool allow, string? reason, CancellationToken cancellationToken)
    {

        bool ok = await _dataSource.ResolveAsync(card.WardId, allow, reason, cancellationToken).ConfigureAwait(true);

        if (!ok)
        {

            LastError = allow ? $"Failed to approve ward {card.WardId}." : $"Failed to deny ward {card.WardId}.";

            _whispers.Show(WhisperSeverity.Error, allow ? "Approve failed." : "Deny failed.");

            return;

        }

        Wards.Remove(card);

        OnPropertyChanged(nameof(HasNoWards));

        _whispers.Show(WhisperSeverity.Success, allow ? "Ward approved." : "Ward denied.");

    }

}
