using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Compendium.Ux.Services;

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

    [ObservableProperty] private bool _httpsEnabled;

    [ObservableProperty] private int _httpsPort;

    [ObservableProperty] private string _httpsCertificatePath = string.Empty;

    [ObservableProperty] private string _httpsPrivateKeyPath = string.Empty;

    [ObservableProperty] private string _httpsCertificatePassword = string.Empty;

    private HostSettings _snapshot = new();

    private LocalCertificateGenerator? _certificateGenerator;

    private IDialogService? _dialogService;

    public IAsyncRelayCommand GenerateLocalCertificateCommand { get; }

    public HostSectionViewModel()
    {

        GenerateLocalCertificateCommand = new AsyncRelayCommand(GenerateLocalCertificateAsync);

    }

    /// <summary>
    /// Supplies the collaborators the parameterless-constructed section needs for the local
    /// certificate action. Called once by <see cref="ConfigurationViewModel"/> after construction so
    /// the section stays newable (and unit-testable) without a DI container.
    /// </summary>
    public void AttachServices(LocalCertificateGenerator certificateGenerator, IDialogService dialogService)
    {

        _certificateGenerator = certificateGenerator;

        _dialogService = dialogService;

    }

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

        HttpsEnabled = settings.Https.Enabled;

        HttpsPort = settings.Https.Port;

        HttpsCertificatePath = settings.Https.CertificatePath ?? string.Empty;

        HttpsPrivateKeyPath = settings.Https.PrivateKeyPath ?? string.Empty;

        HttpsCertificatePassword = settings.Https.CertificatePassword ?? string.Empty;

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

            Https = new HttpsSettings
            {

                Enabled = HttpsEnabled,

                Port = HttpsPort,

                CertificatePath = string.IsNullOrWhiteSpace(HttpsCertificatePath) ? null : HttpsCertificatePath,

                PrivateKeyPath = string.IsNullOrWhiteSpace(HttpsPrivateKeyPath) ? null : HttpsPrivateKeyPath,

                CertificatePassword = string.IsNullOrWhiteSpace(HttpsCertificatePassword) ? null : HttpsCertificatePassword,

            },

        };

    }

    private async Task GenerateLocalCertificateAsync()
    {

        if (_certificateGenerator is null)
        {

            return;

        }

        LocalCertificateResult result = await Task.Run(() => _certificateGenerator.Generate()).ConfigureAwait(true);

        HttpsEnabled = true;

        // Preserve a valid, operator-chosen HTTPS port; only fall back to the 5443 default when the
        // current value is unset or out of range (a fresh section or a bad manual edit).
        if (HttpsPort != ArcanumSettingClamps.HostHttpsPort(HttpsPort))
        {

            HttpsPort = new HttpsSettings().Port;

        }

        HttpsCertificatePath = result.CertificatePath;

        HttpsCertificatePassword = result.Password;

        // A generated PFX bundle carries its own key, so any lingering PEM key path must be cleared.
        HttpsPrivateKeyPath = string.Empty;

        if (_dialogService is not null)
        {

            string message = "A self-signed localhost certificate was generated and HTTPS was enabled.\n\n"
                + string.Join("\n\n", result.Warnings)
                + $"\n\nCertificate: {result.CertificatePath}"
                + $"\nExpires: {result.ExpiresAt:yyyy-MM-dd}"
                + $"\nThumbprint: {result.Thumbprint}";

            await _dialogService.ShowAlertAsync("Local certificate generated", message).ConfigureAwait(true);

        }

    }

}
