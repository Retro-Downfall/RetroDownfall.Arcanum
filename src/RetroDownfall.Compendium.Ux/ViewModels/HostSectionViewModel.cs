using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Services;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class HostSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _port;

    [ObservableProperty] private bool _listenAny;

    [ObservableProperty] private string _corsAllowedOrigins = string.Empty;

    [ObservableProperty] private bool _auditLogEnabled;

    [ObservableProperty] private bool _auditLogRedactToolArguments;

    [ObservableProperty] private bool _httpsEnabled;

    [ObservableProperty] private int _httpsPort;

    [ObservableProperty] private string _httpsCertificatePath = string.Empty;

    [ObservableProperty] private string _httpsPrivateKeyPath = string.Empty;

    [ObservableProperty]
    private string _httpsCertificatePasswordEnvironmentVariable = string.Empty;

    [ObservableProperty] private LogLevel _minLogLevelInBuffer;

    private HostSettings _snapshot = new();

    private Func<LocalCertificateResult>? _certificateGenerator;

    private IDialogService? _dialogService;

    public IAsyncRelayCommand GenerateLocalCertificateCommand { get; }

    public HostSectionViewModel()
    {

        GenerateLocalCertificateCommand = new AsyncRelayCommand(GenerateLocalCertificateAsync);

    }

    public void AttachServices(
        LocalCertificateGenerator certificateGenerator,
        IDialogService dialogService)
    {

        ArgumentNullException.ThrowIfNull(certificateGenerator);

        AttachServices(certificateGenerator.Generate, dialogService);

    }

    internal void AttachServices(
        Func<LocalCertificateResult> certificateGenerator,
        IDialogService dialogService)
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

        AuditLogEnabled = settings.AuditLog.Enabled;

        AuditLogRedactToolArguments = settings.AuditLog.RedactToolArguments;

        HttpsEnabled = settings.Https.Enabled;

        HttpsPort = settings.Https.Port;

        HttpsCertificatePath = settings.Https.CertificatePath ?? string.Empty;

        HttpsPrivateKeyPath = settings.Https.PrivateKeyPath ?? string.Empty;

        HttpsCertificatePasswordEnvironmentVariable =
            settings.Https.CertificatePasswordEnvironmentVariable ?? string.Empty;

        MinLogLevelInBuffer = settings.MinLogLevelInBuffer;

    }

    public HostSettings Build()
    {
        // Validate environment variable name
        if (!ConfigurationInputValidator.TryValidateEnvironmentVariableName(
            HttpsCertificatePasswordEnvironmentVariable,
            out string? envVarError))
        {
            throw new InvalidOperationException(envVarError);
        }

        // Validate CORS origins
        if (!ConfigurationInputValidator.TryValidateCorsOrigins(
            CorsAllowedOrigins,
            out var corsErrors))
        {
            throw new InvalidOperationException(
                $"Invalid CORS origins: {string.Join(", ", corsErrors)}");
        }

        return _snapshot with
        {
            Port = Port,
            ListenAny = ListenAny,
            CorsAllowedOrigins = CorsAllowedOrigins.SplitCsv(),
            AuditLog = _snapshot.AuditLog with
            {
                Enabled = AuditLogEnabled,
                RedactToolArguments = AuditLogRedactToolArguments,
            },
            Https = _snapshot.Https with
            {
                Enabled = HttpsEnabled,
                Port = HttpsPort,
                CertificatePath = NullIfWhiteSpace(HttpsCertificatePath),
                PrivateKeyPath = NullIfWhiteSpace(HttpsPrivateKeyPath),
                CertificatePasswordEnvironmentVariable =
                    NullIfWhiteSpace(HttpsCertificatePasswordEnvironmentVariable),
            },
            MinLogLevelInBuffer = MinLogLevelInBuffer,
        };
    }

    private async Task GenerateLocalCertificateAsync()
    {

        if (_certificateGenerator is null)
        {

            return;

        }

        Func<LocalCertificateResult> generate = _certificateGenerator;

        LocalCertificateResult result;

        try
        {

            result = await Task
                .Run(generate)
                .ConfigureAwait(true);

        }
        catch (Exception ex)
        {

            // Generation writes to disk and adjusts file modes, so it can fail for reasons entirely
            // outside Compendium. Report it and leave HTTPS and the certificate paths untouched rather
            // than letting the fault reach the dispatcher and terminate the app with unsaved edits.
            if (_dialogService is not null)
            {

                await _dialogService
                    .ShowAlertAsync(
                        "Could not generate certificate",
                        $"Compendium could not write a self-signed localhost certificate to {ArcanumPaths.CertificatesDirectory}."
                        + $"{Environment.NewLine}{Environment.NewLine}{ex.Message}")
                    .ConfigureAwait(true);

            }

            return;

        }

        HttpsEnabled = true;

        if (HttpsPort != ArcanumSettingClamps.HostHttpsPort(HttpsPort))
        {

            HttpsPort = new HttpsSettings().Port;

        }

        HttpsCertificatePath = result.CertificatePath;

        HttpsPrivateKeyPath = result.PrivateKeyPath;

        HttpsCertificatePasswordEnvironmentVariable = string.Empty;

        if (_dialogService is null)
        {

            return;

        }

        string message = "A self-signed localhost certificate was generated and HTTPS was enabled."
            + "\n\n"
            + string.Join("\n\n", result.Warnings)
            + $"\n\nCertificate: {result.CertificatePath}"
            + $"\nExpires: {result.ExpiresAt:yyyy-MM-dd}"
            + $"\nThumbprint: {result.Thumbprint}";

        await _dialogService
            .ShowAlertAsync("Local certificate generated", message)
            .ConfigureAwait(true);

    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value;

}
