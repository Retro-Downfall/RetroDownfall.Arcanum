using System.Net;
using System.Security.Cryptography.X509Certificates;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Compendium.Ux.Services;
using RetroDownfall.Compendium.Ux.ViewModels;
using Xunit;

namespace RetroDownfall.Compendium.Ux.Tests.Compendium;

[Collection("EnvVarSensitive")]
public sealed class HttpsConfigurationTests : IDisposable
{
    private const string CertificatePasswordVariable =
        "ARCANUM_TEST_COMPENDIUM_CERT_PASSWORD";

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string? _originalCertificatePassword;

    private readonly string _tempRoot;

    public HttpsConfigurationTests()
    {

        _originalHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _originalCertificatePassword =
            Environment.GetEnvironmentVariable(CertificatePasswordVariable);

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-https-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        Environment.SetEnvironmentVariable("HOME", _tempRoot);

        Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

        Environment.SetEnvironmentVariable(CertificatePasswordVariable, null);

    }

    [Fact]
    public void Generate_writes_owner_only_loadable_localhost_pem_pair()
    {

        LocalCertificateGenerator generator = new();

        string certificatesDirectory = Path.Combine(_tempRoot, "certs");

        LocalCertificateResult result = generator.Generate(certificatesDirectory, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(result.CertificatePath));

        Assert.True(File.Exists(result.PrivateKeyPath));

        Assert.NotEmpty(result.Warnings);

        using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(
            result.CertificatePath,
            result.PrivateKeyPath);

        Assert.Contains("CN=localhost", certificate.Subject, StringComparison.OrdinalIgnoreCase);

        Assert.True(certificate.HasPrivateKey);

        X509BasicConstraintsExtension? basicConstraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();

        Assert.NotNull(basicConstraints);

        Assert.False(basicConstraints.CertificateAuthority);

        bool hasServerAuth = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>())
            .Any(o => o.Value == "1.3.6.1.5.5.7.3.1");

        Assert.True(hasServerAuth);

        X509SubjectAlternativeNameExtension? san = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .FirstOrDefault();

        Assert.NotNull(san);

        // Prefer typed SAN enumeration — X509SubjectAlternativeNameExtension.Format()
        // may expand IPv6 loopback as 0000:…:0001 depending on runtime/OS.
        Assert.Contains("localhost", san.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);

        IPAddress[] sanIps = san.EnumerateIPAddresses().ToArray();

        Assert.Contains(IPAddress.Loopback, sanIps);

        Assert.Contains(IPAddress.IPv6Loopback, sanIps);

        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);

        Assert.False(string.IsNullOrWhiteSpace(result.Thumbprint));

    }

    [Fact]
    public async Task GenerateLocalCertificateCommand_enables_https_sets_paths_and_preserves_valid_port()
    {

        HostSectionViewModel host = new();

        host.AttachServices(new LocalCertificateGenerator(), new NoopDialogService());

        host.LoadFrom(new HostSettings { Https = new HttpsSettings { Port = 8443 } });

        host.HttpsPrivateKeyPath = "/should/be/cleared.key";

        await host.GenerateLocalCertificateCommand.ExecuteAsync(null);

        Assert.True(host.HttpsEnabled);

        Assert.Equal(8443, host.HttpsPort);

        Assert.False(string.IsNullOrWhiteSpace(host.HttpsCertificatePath));

        Assert.False(string.IsNullOrWhiteSpace(host.HttpsPrivateKeyPath));

        Assert.Equal(
            string.Empty,
            host.HttpsCertificatePasswordEnvironmentVariable);

    }

    [Fact]
    public async Task GenerateLocalCertificateCommand_falls_back_to_default_port_when_invalid()
    {

        HostSectionViewModel host = new();

        host.AttachServices(new LocalCertificateGenerator(), new NoopDialogService());

        host.LoadFrom(new HostSettings { Https = new HttpsSettings { Port = 0 } });

        await host.GenerateLocalCertificateCommand.ExecuteAsync(null);

        Assert.Equal(new HttpsSettings().Port, host.HttpsPort);

    }

    [Fact]
    public async Task Save_persists_certificate_reference_without_secret_material()
    {
        Environment.SetEnvironmentVariable(
            CertificatePasswordVariable,
            "plaintext-pfx-password");

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        LocalCertificateGenerator generator = new();

        LocalCertificateResult certificate = generator.Generate(ArcanumPaths.CertificatesDirectory, DateTimeOffset.UtcNow);

        ArcanumConfigurationStore store = new();

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                Https = new HttpsSettings
                {
                    Enabled = true,
                    Port = 5443,
                    CertificatePath = certificate.CertificatePath,
                    PrivateKeyPath = certificate.PrivateKeyPath,
                    CertificatePasswordEnvironmentVariable =
                        CertificatePasswordVariable,
                },
            },
        };

        ConfigurationWriteResult writeResult = await store.WriteAsync(settings);

        Assert.True(
            writeResult.IsSuccess,
            writeResult.ErrorMessage
            ?? string.Join("; ", writeResult.ValidationErrors.Select(e => $"{e.Pointer}: {e.Detail}")));

        string rawJson = await File.ReadAllTextAsync(store.ConfigurationFilePath);

        Assert.DoesNotContain("plaintext-pfx-password", rawJson);

        Assert.Contains(CertificatePasswordVariable, rawJson);

        ArcanumSettings reloaded = await store.ReadAsync();

        Assert.Equal(
            CertificatePasswordVariable,
            reloaded.Host.Https.CertificatePasswordEnvironmentVariable);

    }

    public void Dispose()
    {

        Environment.SetEnvironmentVariable("HOME", _originalHome);

        Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

        Environment.SetEnvironmentVariable(
            CertificatePasswordVariable,
            _originalCertificatePassword);

        try
        {

            if (Directory.Exists(_tempRoot))
            {

                Directory.Delete(_tempRoot, recursive: true);

            }

        }
        catch
        {
            // best-effort cleanup
        }

    }

    private sealed class NoopDialogService : IDialogService
    {

        public Task ShowAlertAsync(string title, string message, string cancel = "OK") => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string accept = "Yes", string cancel = "No")
            => Task.FromResult(true);

    }

}
