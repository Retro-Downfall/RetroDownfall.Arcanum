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

    private readonly string _originalHome;

    private readonly string _originalUserProfile;

    private readonly string _tempRoot;

    public HttpsConfigurationTests()
    {

        _originalHome = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;

        _originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

        _tempRoot = Path.Combine(Path.GetTempPath(), $"compendium-https-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        Environment.SetEnvironmentVariable("HOME", _tempRoot);

        Environment.SetEnvironmentVariable("USERPROFILE", _tempRoot);

    }

    [Fact]
    public void Generate_writes_owner_only_loadable_localhost_pfx()
    {

        LocalCertificateGenerator generator = new();

        string certificatesDirectory = Path.Combine(_tempRoot, "certs");

        LocalCertificateResult result = generator.Generate(certificatesDirectory, DateTimeOffset.UtcNow);

        Assert.True(File.Exists(result.CertificatePath));

        Assert.False(string.IsNullOrWhiteSpace(result.Password));

        Assert.NotEmpty(result.Warnings);

        X509KeyStorageFlags keyStorageFlags = OperatingSystem.IsMacOS()
            ? X509KeyStorageFlags.DefaultKeySet
            : X509KeyStorageFlags.EphemeralKeySet;

        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
            result.CertificatePath,
            result.Password,
            keyStorageFlags);

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

        string sanText = san.Format(multiLine: false);

        Assert.Contains("localhost", sanText, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("127.0.0.1", sanText, StringComparison.Ordinal);

        Assert.True(
            sanText.Contains("::1", StringComparison.Ordinal)
            || sanText.Contains("0:0:0:0:0:0:0:1", StringComparison.OrdinalIgnoreCase),
            $"Expected IPv6 loopback in SAN, got: {sanText}");

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

        Assert.False(string.IsNullOrWhiteSpace(host.HttpsCertificatePassword));

        Assert.Equal(string.Empty, host.HttpsPrivateKeyPath);

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
    public async Task Save_encrypts_https_certificate_password_at_rest()
    {

        _ = Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        LocalCertificateGenerator generator = new();

        LocalCertificateResult certificate = generator.Generate(ArcanumPaths.CertificatesDirectory, DateTimeOffset.UtcNow);

        ArcanumDataProtectionSecretProtector protector = new();

        ArcanumConfigurationStore store = new(protector);

        ArcanumSettings settings = new()
        {
            Host = new HostSettings
            {
                Https = new HttpsSettings
                {
                    Enabled = true,
                    Port = 5443,
                    CertificatePath = certificate.CertificatePath,
                    CertificatePassword = "plaintext-pfx-password",
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

        Assert.Contains("dp:v1:", rawJson);

        ArcanumSettings reloaded = await store.ReadAsync();

        Assert.Equal("plaintext-pfx-password", reloaded.Host.Https.CertificatePassword);

    }

    public void Dispose()
    {

        Environment.SetEnvironmentVariable("HOME", _originalHome);

        Environment.SetEnvironmentVariable("USERPROFILE", _originalUserProfile);

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
