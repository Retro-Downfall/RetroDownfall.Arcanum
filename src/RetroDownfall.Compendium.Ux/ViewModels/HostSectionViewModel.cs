using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class HostSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _port;

    [ObservableProperty] private bool _listenAny;

    [ObservableProperty] private string _corsAllowedOrigins = string.Empty;

    [ObservableProperty] private bool _enableScalarUi;

    [ObservableProperty] private long _maxRequestBodyBytes;

    [ObservableProperty] private string _workspace = string.Empty;

    [ObservableProperty] private string _systemFingerprint = string.Empty;

    [ObservableProperty] private int _retainedLogFileCount;

    [ObservableProperty] private bool _enableEnterpriseTelemetry;

    [ObservableProperty] private bool _rateLimitEnabled;

    [ObservableProperty] private int _rateLimitPermitLimit;

    [ObservableProperty] private int _rateLimitWindowSeconds;

    [ObservableProperty] private int _rateLimitQueueLimit;

    private HostSettings _snapshot = new();

    public void LoadFrom(HostSettings settings)
    {

        _snapshot = settings;

        Port = settings.Port;

        ListenAny = settings.ListenAny;

        CorsAllowedOrigins = settings.CorsAllowedOrigins.JoinCsv();

        EnableScalarUi = settings.EnableScalarUi;

        MaxRequestBodyBytes = settings.MaxRequestBodyBytes;

        Workspace = settings.Workspace ?? string.Empty;

        SystemFingerprint = settings.SystemFingerprint ?? string.Empty;

        RetainedLogFileCount = settings.RetainedLogFileCount;

        EnableEnterpriseTelemetry = settings.EnableEnterpriseTelemetry;

        RateLimitEnabled = settings.RateLimit.Enabled;

        RateLimitPermitLimit = settings.RateLimit.PermitLimit;

        RateLimitWindowSeconds = settings.RateLimit.WindowSeconds;

        RateLimitQueueLimit = settings.RateLimit.QueueLimit;

    }

    public HostSettings Build()
    {

        return _snapshot with
        {

            Port = Port,

            ListenAny = ListenAny,

            CorsAllowedOrigins = CorsAllowedOrigins.SplitCsv(),

            EnableScalarUi = EnableScalarUi,

            MaxRequestBodyBytes = MaxRequestBodyBytes,

            Workspace = string.IsNullOrWhiteSpace(Workspace) ? null : Workspace,

            SystemFingerprint = string.IsNullOrWhiteSpace(SystemFingerprint) ? null : SystemFingerprint,

            RetainedLogFileCount = RetainedLogFileCount,

            EnableEnterpriseTelemetry = EnableEnterpriseTelemetry,

            RateLimit = new HostRateLimitSettings
            {

                Enabled = RateLimitEnabled,

                PermitLimit = RateLimitPermitLimit,

                WindowSeconds = RateLimitWindowSeconds,

                QueueLimit = RateLimitQueueLimit,

            },

        };

    }

}
