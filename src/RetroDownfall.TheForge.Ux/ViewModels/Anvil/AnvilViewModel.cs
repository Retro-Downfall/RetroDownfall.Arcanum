using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Anvil;

/// <summary>
/// Status-bar ViewModel for The Anvil. Mirrors connection state and aggregates budget, wards,
/// apprentices, and MCP online/total on a 10s timer (and on connection-state changes).
/// </summary>
public sealed partial class AnvilViewModel : ViewModelBase, IDisposable
{

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IArcanumConnection _connection;

    private readonly IAnvilDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly ITheForgeApiKeyProvider _apiKeyProvider;

    private readonly IOptionsMonitor<TheForgeSettings> _settings;

    private readonly ILogger<AnvilViewModel> _logger;

    private readonly IDisposable? _settingsSubscription;

    private CancellationTokenSource? _refreshCts;

    private bool _disposed;

    [ObservableProperty]
    private ConnectionState _connectionState;

    [ObservableProperty]
    private string _activeCampaignName = "No campaign";

    [ObservableProperty]
    private string _activeModelName = "No model";

    [ObservableProperty]
    private double _manaPercent;

    [ObservableProperty]
    private decimal _todaySpendUsd;

    [ObservableProperty]
    private int _activeWardsCount;

    [ObservableProperty]
    private int _runningApprenticesCount;

    [ObservableProperty]
    private string _mcpOnlineTotal = "0/0";

    [ObservableProperty]
    private string? _lastRefreshError;

    public AnvilViewModel(
        IArcanumConnection connection,
        IAnvilDataSource dataSource,
        INavigationService navigation,
        ITheForgeApiKeyProvider apiKeyProvider,
        IOptionsMonitor<TheForgeSettings> settings,
        ILogger<AnvilViewModel> logger)
    {

        _connection = connection;

        _dataSource = dataSource;

        _navigation = navigation;

        _apiKeyProvider = apiKeyProvider;

        _settings = settings;

        _logger = logger;

        Title = "The Anvil";

        _connectionState = connection.State;

        _connection.PropertyChanged += OnConnectionPropertyChanged;

        ApplyCampaignFromSettings(_settings.CurrentValue);

        _settingsSubscription = _settings.OnChange(ApplyCampaignFromSettings);

        StartRefreshLoop();

    }

    public string ConnectionStatusText => ConnectionState switch
    {
        ConnectionState.Connected => "Arcanum connected",
        ConnectionState.Connecting => "Seeking Arcanum...",
        ConnectionState.Error when IsMissingApiKey => "API key required",
        ConnectionState.Error when IsTimeout => "Arcanum timed out",
        ConnectionState.Error when IsConnectionFailed => "Arcanum connection failed",
        ConnectionState.Error => "Arcanum unreachable",
        _ => "Arcanum disconnected",
    };

    public bool ShowEnterApiKey => IsMissingApiKey;

    private bool IsMissingApiKey =>
        string.Equals(_connection.LastErrorCode, "Security.MissingApiKey", StringComparison.Ordinal);

    private bool IsTimeout =>
        string.Equals(_connection.LastErrorCode, "Connection.Timeout", StringComparison.Ordinal);

    private bool IsConnectionFailed =>
        string.Equals(_connection.LastErrorCode, "Connection.Failed", StringComparison.Ordinal);

    [RelayCommand]
    private void FocusConnection() => _navigation.FocusPanel(PanelKind.Anvil);

    [RelayCommand]
    private void Connect() => _connection.Connect();

    [RelayCommand]
    private void Disconnect() => _connection.Disconnect();

    [RelayCommand]
    private void EnterApiKey()
    {

        _apiKeyProvider.ClearPasteDecline();

        _connection.Connect();

    }

    [RelayCommand]
    private void FocusCampaign() => _navigation.FocusPanel(PanelKind.Atelier);

    [RelayCommand]
    private void FocusModel() => _navigation.FocusPanel(PanelKind.Arsenal);

    [RelayCommand]
    private void FocusMana() => _navigation.FocusPanel(PanelKind.Treasury);

    [RelayCommand]
    private void FocusBudget() => _navigation.FocusPanel(PanelKind.Treasury);

    [RelayCommand]
    private void FocusWards() => _navigation.FocusPanel(PanelKind.Gatehouse);

    [RelayCommand]
    private void FocusApprentices() => _navigation.FocusPanel(PanelKind.WarTable);

    [RelayCommand]
    private void FocusMcp() => _navigation.FocusPanel(PanelKind.Arsenal);

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        if (ConnectionState != ConnectionState.Connected)
        {

            return;

        }

        try
        {

            BudgetSummaryDto? budget = await _dataSource.GetBudgetAsync(cancellationToken).ConfigureAwait(true);

            if (budget is not null)
            {

                TodaySpendUsd = budget.TodaySpendUsd;

                ManaPercent = Math.Clamp(budget.SpentPercent, 0, 100);

            }

            ActiveWardsCount = (await _dataSource.ListWardsAsync(cancellationToken).ConfigureAwait(true)).Count;

            IReadOnlyList<ApprenticeSummaryDto> apprentices = await _dataSource
                .ListApprenticesAsync(cancellationToken)
                .ConfigureAwait(true);

            RunningApprenticesCount = apprentices.Count(static a =>
                string.Equals(a.Status, "Running", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Status, "InProgress", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Status, "in_progress", StringComparison.OrdinalIgnoreCase));

            IReadOnlyList<McpServerInfo> mcp = await _dataSource.ListMcpServersAsync(cancellationToken).ConfigureAwait(true);

            int online = mcp.Count(static s => s.State == McpServerState.Running);

            McpOnlineTotal = $"{online}/{mcp.Count}";

            if (_connection.LastReport is { } report)
            {

                HealthComponentDto? model = report.Components
                    .FirstOrDefault(static c =>
                        c.Name.Contains("model", StringComparison.OrdinalIgnoreCase)
                        || c.Name.Contains("llama", StringComparison.OrdinalIgnoreCase)
                        || c.Name.Contains("provider", StringComparison.OrdinalIgnoreCase));

                if (model is not null && !string.IsNullOrWhiteSpace(model.Detail))
                {

                    ActiveModelName = model.Detail!;

                }

            }

            LastRefreshError = null;

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Expected on dispose / disconnect.

        }
        catch (Exception ex)
        {

            _logger.LogWarning(ex, "Anvil refresh aggregation failed; keeping last known values.");

            LastRefreshError = ex.Message;

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

        _settingsSubscription?.Dispose();

        _refreshCts?.Cancel();

        _refreshCts?.Dispose();

        GC.SuppressFinalize(this);

    }

    private void StartRefreshLoop()
    {

        _refreshCts?.Cancel();

        _refreshCts?.Dispose();

        _refreshCts = new CancellationTokenSource();

        TaskUtilities.FireAndForget(RunRefreshLoopAsync(_refreshCts.Token), _logger);

    }

    private async Task RunRefreshLoopAsync(CancellationToken cancellationToken)
    {

        await RefreshAsync(cancellationToken).ConfigureAwait(true);

        using PeriodicTimer timer = new(RefreshInterval);

        try
        {

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {

                await RefreshAsync(cancellationToken).ConfigureAwait(true);

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Expected on dispose.

        }

    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(IArcanumConnection.State)
            or nameof(IArcanumConnection.LastErrorCode)
            or nameof(IArcanumConnection.LastErrorMessage))
        {

            ConnectionState = _connection.State;

            OnPropertyChanged(nameof(ConnectionStatusText));

            OnPropertyChanged(nameof(ShowEnterApiKey));

            if (e.PropertyName == nameof(IArcanumConnection.State)
                && ConnectionState == ConnectionState.Connected)
            {

                TaskUtilities.FireAndForget(RefreshAsync(CancellationToken.None), _logger);

            }

        }

    }

    private void ApplyCampaignFromSettings(TheForgeSettings settings)
    {

        ActiveCampaignName = settings.LastCampaignId is { } id
            ? $"Campaign {id:D}"
            : "No campaign";

    }

    partial void OnConnectionStateChanged(ConnectionState value)
    {

        OnPropertyChanged(nameof(ConnectionStatusText));

        OnPropertyChanged(nameof(ShowEnterApiKey));

    }

}
