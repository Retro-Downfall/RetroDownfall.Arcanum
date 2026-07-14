using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Treasury;

/// <summary>
/// The Treasury — read-only budget dashboard over <c>GET /api/budget</c>. Shows enabled state, daily
/// limit, today's spend, remaining, spent percent (ManaBar), and alert threshold. Refreshes on connect.
/// Budget/pricing editing is out of scope for the alpha.
/// </summary>
public sealed partial class TreasuryViewModel : ViewModelBase, IDisposable
{

    private readonly IArcanumConnection _connection;

    private readonly ITreasuryDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    private bool _disposed;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private DateTimeOffset? _lastUpdated;

    [ObservableProperty]
    private BudgetSummaryDto? _budget;

    public TreasuryViewModel(IArcanumConnection connection, ITreasuryDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _connection = connection;

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "The Treasury";

        _connection.PropertyChanged += OnConnectionPropertyChanged;

    }

    public bool IsEnabled => Budget?.Enabled ?? false;

    public decimal DailyLimitUsd => Budget?.DailyLimitUsd ?? 0m;

    public decimal TodaySpendUsd => Budget?.TodaySpendUsd ?? 0m;

    public decimal RemainingUsd => Budget?.RemainingUsd ?? 0m;

    public int SpentPercent => Budget is { } b ? Math.Clamp(b.SpentPercent, 0, 100) : 0;

    public int AlertThresholdPercent => Budget?.AlertThresholdPercent ?? 0;

    public string EmptyState => IsEnabled ? string.Empty : "Budget enforcement is disabled.";

    partial void OnBudgetChanged(BudgetSummaryDto? value)
    {

        OnPropertyChanged(nameof(IsEnabled));

        OnPropertyChanged(nameof(DailyLimitUsd));

        OnPropertyChanged(nameof(TodaySpendUsd));

        OnPropertyChanged(nameof(RemainingUsd));

        OnPropertyChanged(nameof(SpentPercent));

        OnPropertyChanged(nameof(AlertThresholdPercent));

        OnPropertyChanged(nameof(EmptyState));

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            BudgetSummaryDto? budget = await _dataSource.GetBudgetAsync(cancellationToken).ConfigureAwait(true);

            Budget = budget;

            LastUpdated = DateTimeOffset.Now;

            if (budget is null)
            {

                StatusText = "Budget unavailable.";

                LastError = "Budget is unavailable.";

                _foundryFloor.AppendLine("Treasury: budget unavailable.");

            }

            else
            {

                StatusText = budget.Enabled ? "Budget loaded." : "Budget enforcement is disabled.";

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Treasury refresh error: {ex.Message}");

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

        _connection.PropertyChanged -= OnConnectionPropertyChanged;

        GC.SuppressFinalize(this);

    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(IArcanumConnection.State) && _connection.State == ConnectionState.Connected)
        {

            TaskUtilities.FireAndForget(RefreshAsync(CancellationToken.None));

        }

    }

}
