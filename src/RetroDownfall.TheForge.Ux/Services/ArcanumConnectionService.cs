using Avalonia.Threading;
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
/// <see cref="TheForgeSettings.AutoConnect"/> is set) in DI so The Anvil's connection indicator reflects
/// live server state throughout the app's lifetime. Reacts to <c>the-forge.json</c> changes (base URL,
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

    [ObservableProperty]
    private InstanceMetadataDto? _lastMeta;

    [ObservableProperty]
    private string? _lastErrorCode;

    [ObservableProperty]
    private string? _lastErrorMessage;

    private readonly ArcanumApiClient _apiClient;

    private readonly IOptionsMonitor<TheForgeSettings> _settingsMonitor;

    private readonly ILogger<ArcanumConnectionService> _logger;

    private readonly IDisposable? _settingsChangeSubscription;

    /// <summary>
    /// Guards every <see cref="_pollCts"/> transition and the <see cref="_lastConnectionSettings"/> swap.
    /// Connect/Disconnect run on both the Avalonia UI thread and the <c>the-forge.json</c> reload thread
    /// (<see cref="IOptionsMonitor{TOptions}.OnChange"/>), and cancelling an already-disposed
    /// <see cref="CancellationTokenSource"/> throws.
    /// </summary>
    private readonly object _pollGate = new();

    private CancellationTokenSource? _pollCts;

    private int _consecutiveFailures;

    /// <summary>
    /// Last settings snapshot used for reconnect decisions. Layout/theme/campaign patches must not
    /// restart the health poller.
    /// </summary>
    private TheForgeSettings _lastConnectionSettings;

    public ArcanumConnectionService(
        ArcanumApiClient apiClient,
        IOptionsMonitor<TheForgeSettings> settingsMonitor,
        ILogger<ArcanumConnectionService> logger)
    {

        _apiClient = apiClient;

        _settingsMonitor = settingsMonitor;

        _logger = logger;

        _lastConnectionSettings = settingsMonitor.CurrentValue;

        _settingsChangeSubscription = settingsMonitor.OnChange(OnSettingsChanged);

        // Do not AutoConnect from the ctor: DI resolves this singleton while building MainViewModel,
        // before App assigns desktop.MainWindow. Starting polls that early permanently races the
        // API-key paste prompt (MainWindow is still null). Call StartAutoConnectIfConfigured() after
        // the main window exists.

    }

    /// <summary>
    /// Starts the health poller when <see cref="TheForgeSettings.AutoConnect"/> is enabled.
    /// Must be called after the Avalonia main window is assigned so API-key prompts can show.
    /// </summary>
    public void StartAutoConnectIfConfigured()
    {

        if (_settingsMonitor.CurrentValue.AutoConnect)
        {

            Connect();

        }

    }

    /// <summary>
    /// Starts (or restarts) the health poll loop. Idempotent: if already polling, cancels the
    /// previous loop and starts a fresh one (true reconnect). Always surfaces Connecting until the
    /// next successful authenticated health poll — never claims Connected from a prior success.
    /// </summary>
    public void Connect()
    {

        CancellationToken pollToken;

        lock (_pollGate)
        {

            if (_pollCts is not null)
            {

                _pollCts.Cancel();

                _pollCts.Dispose();

                _pollCts = null;

            }

            CancellationTokenSource cts = new();

            _pollCts = cts;

            // Capture from the local, not the field: re-reading _pollCts here would start the loop on a
            // token another thread's Connect()/Disconnect() had already replaced or nulled.
            pollToken = cts.Token;

        }

        PostToUi(() =>
        {
            _consecutiveFailures = 0;

            State = ConnectionState.Connecting;

            LastErrorCode = null;

            LastErrorMessage = null;
        });

        _ = PollLoopAsync(pollToken);

    }

    public void Disconnect()
    {

        lock (_pollGate)
        {

            _pollCts?.Cancel();

            _pollCts?.Dispose();

            _pollCts = null;

        }

        PostToUi(() => State = ConnectionState.Disconnected);

    }

    private void OnSettingsChanged(TheForgeSettings settings)
    {

        TheForgeSettings previous;

        lock (_pollGate)
        {

            previous = _lastConnectionSettings;

            _lastConnectionSettings = settings;

        }

        // the-forge.json also stores LayoutState, Theme, LastCampaignId, and may strip ApiKey after
        // keychain migration. Reloading those must not call Connect().
        if (ConnectionSettingsUnchanged(previous, settings))
        {

            return;

        }

        if (settings.AutoConnect)
        {

            Connect();

        }
        else
        {

            Disconnect();

        }

    }

    /// <summary>
    /// True when BaseUrl and AutoConnect are unchanged. ApiKey is ignored: legacy plaintext keys
    /// are stripped from the-forge.json into the keychain without needing a reconnect.
    /// </summary>
    internal static bool ConnectionSettingsUnchanged(TheForgeSettings previous, TheForgeSettings next) =>
        previous.AutoConnect == next.AutoConnect
        && string.Equals(previous.BaseUrl, next.BaseUrl, StringComparison.Ordinal);

    /// <summary>
    /// Auth failures should flip The Anvil to Error immediately (not after three seeks). They are also the
    /// only errors that cannot clear themselves — the resolved key is cached for the process lifetime, so
    /// further polls only repeat the rejection — which is why a waiter may treat this class, and only this
    /// class, as a final answer rather than waiting for a recovery that will never come.
    /// </summary>
    internal static bool IsImmediateError(string? errorCode) =>
        string.Equals(errorCode, "Security.MissingApiKey", StringComparison.Ordinal)
        || string.Equals(errorCode, "Auth.Unauthorized", StringComparison.Ordinal);

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
                .GetAsync("/api/health", TheForgeJsonContext.Default.ApiResponseHealthReportDto, cancellationToken)
                .ConfigureAwait(false);

            if (response is { IsSuccess: true, Data: { } report })
            {

                if (report.Status == HealthStatus.Unhealthy)
                {

                    PostToUi(() =>
                    {
                        LastErrorCode = "Health.Unhealthy";

                        LastErrorMessage = "Arcanum reported unhealthy.";

                        RegisterFailure();
                    });

                    return;

                }

                PostToUi(() =>
                {
                    _consecutiveFailures = 0;

                    LastReport = report;

                    LastErrorCode = null;

                    LastErrorMessage = null;

                    State = ConnectionState.Connected;
                });

                try
                {

                    ApiResponse<InstanceMetadataDto>? meta = await _apiClient
                        .GetAsync("/api/meta", TheForgeJsonContext.Default.ApiResponseInstanceMetadataDto, cancellationToken)
                        .ConfigureAwait(false);

                    if (meta is { IsSuccess: true, Data: not null })
                    {

                        PostToUi(() => LastMeta = meta.Data);

                    }

                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {

                    _logger.LogDebug(ex, "Meta poll failed after successful health.");

                }

                return;

            }

            string code = response?.Error?.Code ?? "Connection.Failed";

            string message = response?.Error?.Message ?? "Health check failed.";

            PostToUi(() =>
            {
                LastErrorCode = code;

                LastErrorMessage = message;

                RegisterFailure();
            });

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            _logger.LogDebug(ex, "Health poll failed.");

            string code;

            string message;

            if (ex is TaskCanceledException or TimeoutException)
            {

                code = "Connection.Timeout";

                message = "The request to Arcanum timed out.";

            }
            else
            {

                code = "Connection.Failed";

                message = ex.Message;

            }

            PostToUi(() =>
            {
                LastErrorCode = code;

                LastErrorMessage = message;

                RegisterFailure();
            });

        }

    }

    private void RegisterFailure()
    {

        _consecutiveFailures++;

        State = IsImmediateError(LastErrorCode) || _consecutiveFailures >= ConsecutiveFailuresBeforeError
            ? ConnectionState.Error
            : ConnectionState.Connecting;

    }

    private static void PostToUi(Action action)
    {

        if (Dispatcher.UIThread.CheckAccess())
        {

            action();

            return;

        }

        Dispatcher.UIThread.Post(action);

    }

    public void Dispose()
    {

        Disconnect();

        _settingsChangeSubscription?.Dispose();

    }

}
