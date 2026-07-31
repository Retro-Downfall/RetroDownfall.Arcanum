using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.Arcanum.Core.Desktop;
using RetroDownfall.TheForge.Ux.Services.Whispers;
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

    private readonly IClipboardService _clipboard;

    private readonly ICompendiumLauncher _compendiumLauncher;

    private readonly IWhispersService _whispers;

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

    public TreasuryViewModel(
        IArcanumConnection connection,
        ITreasuryDataSource dataSource,
        FoundryFloorViewModel foundryFloor,
        IClipboardService clipboard,
        ICompendiumLauncher compendiumLauncher,
        IWhispersService whispers)
    {

        _connection = connection;

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        _clipboard = clipboard;

        _compendiumLauncher = compendiumLauncher;

        _whispers = whispers;

        Title = "The Treasury";

        _connection.PropertyChanged += OnConnectionPropertyChanged;

    }

    public bool IsEnabled => Budget?.Enabled ?? false;

    public decimal DailyLimitUsd => Budget?.DailyLimitUsd ?? 0m;

    public decimal TodaySpendUsd => Budget?.TodaySpendUsd ?? 0m;

    public decimal RemainingUsd => Budget?.RemainingUsd ?? 0m;

    public int SpentPercent => Budget is { } b ? Math.Clamp(b.SpentPercent, 0, 100) : 0;

    public int AlertThresholdPercent => Budget?.AlertThresholdPercent ?? 0;

    public string BudgetDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Budget enforcement", DisabledSettingPaths.Budget);

    public string EmptyState => IsEnabled ? string.Empty : BudgetDisabledMessage;

    [RelayCommand]
    private async Task CopyDisabledPathsAsync(CancellationToken cancellationToken) =>
        await _clipboard
            .SetTextAsync(DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.Budget), cancellationToken)
            .ConfigureAwait(true);

    [RelayCommand]
    private void OpenCompendium()
    {

        CompendiumLaunchResult result = _compendiumLauncher.TryLaunch();

        _whispers.Show(
            result.Launched ? WhisperSeverity.Success : WhisperSeverity.Warning,
            result.Message);

    }

    partial void OnBudgetChanged(BudgetSummaryDto? value)
    {

        OnPropertyChanged(nameof(IsEnabled));

        OnPropertyChanged(nameof(DailyLimitUsd));

        OnPropertyChanged(nameof(TodaySpendUsd));

        OnPropertyChanged(nameof(RemainingUsd));

        OnPropertyChanged(nameof(SpentPercent));

        OnPropertyChanged(nameof(AlertThresholdPercent));

        OnPropertyChanged(nameof(BudgetDisabledMessage));

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

                StatusText = budget.Enabled ? "Budget loaded." : BudgetDisabledMessage;

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
