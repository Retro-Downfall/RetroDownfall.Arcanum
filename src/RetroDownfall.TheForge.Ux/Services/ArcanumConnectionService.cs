using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Singleton background poller for <c>GET /api/health</c>, registered once and started (when
/// <see cref="ForgeSettings.AutoConnect"/> is set) in DI so The Anvil's connection indicator reflects
/// live server state throughout the app's lifetime. Reacts to <c>forge.json</c> changes (base URL,
/// API key, AutoConnect toggle) via <see cref="IOptionsMonitor{TOptions}"/>.
/// </summary>
public sealed partial class ArcanumConnectionService : ObservableObject, IArcanumConnection, IDisposable
{

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private const int ConsecutiveFailuresBeforeError = 3;

    [ObservableProperty]
    private ConnectionState _state = ConnectionState.Disconnected;

    [ObservableProperty]
    private HealthReportDto? _lastReport;

    private readonly ArcanumApiClient _apiClient;

    private readonly ILogger<ArcanumConnectionService> _logger;

    private readonly IDisposable? _settingsChangeSubscription;

    private CancellationTokenSource? _pollCts;

    private int _consecutiveFailures;

    public ArcanumConnectionService(
        ArcanumApiClient apiClient,
        IOptionsMonitor<ForgeSettings> settingsMonitor,
        ILogger<ArcanumConnectionService> logger)
    {

        _apiClient = apiClient;

        _logger = logger;

        _settingsChangeSubscription = settingsMonitor.OnChange(OnSettingsChanged);

        if (settingsMonitor.CurrentValue.AutoConnect)
        {

            Connect();

        }

    }

    public void Connect()
    {

        if (_pollCts is not null)
        {

            return;

        }

        _pollCts = new CancellationTokenSource();

        State = ConnectionState.Connecting;

        _consecutiveFailures = 0;

        _ = PollLoopAsync(_pollCts.Token);

    }

    public void Disconnect()
    {

        _pollCts?.Cancel();

        _pollCts?.Dispose();

        _pollCts = null;

        State = ConnectionState.Disconnected;

    }

    private void OnSettingsChanged(ForgeSettings settings)
    {

        if (settings.AutoConnect)
        {

            Connect();

        }
        else
        {

            Disconnect();

        }

    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {

        using PeriodicTimer timer = new(PollInterval);

        try
        {

            do
            {

                await PollOnceAsync(cancellationToken).ConfigureAwait(false);

            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));

        }
        catch (OperationCanceledException)
        {

            // Expected on Disconnect()/shutdown.

        }

    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {

        try
        {

            ApiResponse<HealthReportDto>? response = await _apiClient
                .GetAsync("/api/health", ForgeJsonContext.Default.ApiResponseHealthReportDto, cancellationToken)
                .ConfigureAwait(false);

            if (response is { IsSuccess: true })
            {

                _consecutiveFailures = 0;

                LastReport = response.Data;

                State = ConnectionState.Connected;

                return;

            }

            RegisterFailure();

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            _logger.LogDebug(ex, "Health poll failed.");

            RegisterFailure();

        }

    }

    private void RegisterFailure()
    {

        _consecutiveFailures++;

        State = _consecutiveFailures >= ConsecutiveFailuresBeforeError
            ? ConnectionState.Error
            : ConnectionState.Connecting;

    }

    public void Dispose()
    {

        Disconnect();

        _settingsChangeSubscription?.Dispose();

    }

}
