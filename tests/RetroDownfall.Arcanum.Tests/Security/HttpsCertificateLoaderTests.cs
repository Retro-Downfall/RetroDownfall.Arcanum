using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class HttpsCertificateLoaderTests : IDisposable
{

    private readonly string _tempRoot;

    public HttpsCertificateLoaderTests()
    {

        _tempRoot = Path.Combine(Path.GetTempPath(), $"arcanum-https-loader-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

    }

    [Fact]
    public void Load_PfxWithPlaintextPassword_Succeeds()
    {

        (string path, string password) = CreatePfx(password: "test-password");

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings { CertificatePath = path, CertificatePassword = password },
            secretProtector: null);

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Certificate);

        Assert.True(result.Certificate.HasPrivateKey);

    }

    [Fact]
    public void Load_PasswordlessPfx_Succeeds()
    {

        (string path, _) = CreatePfx(password: null);

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings { CertificatePath = path, CertificatePassword = null },
            secretProtector: null);

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Certificate);

    }

    [Fact]
    public void Load_PfxWithDataProtectedPassword_Succeeds()
    {

        (string path, string password) = CreatePfx(password: "protected-secret");

        IDataProtectionProvider provider = DataProtectionProvider.Create("Arcanum.Tests");

        ConfigurationSecretProtector protector = new(provider);

        string? protectedPassword = protector.Protect(password);

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings { CertificatePath = path, CertificatePassword = protectedPassword },
            protector);

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Certificate);

    }

    [Fact]
    public void Load_WrongPfxPassword_ReturnsSanitizedFailure()
    {

        (string path, _) = CreatePfx(password: "correct");

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings { CertificatePath = path, CertificatePassword = "wrong" },
            secretProtector: null);

        Assert.False(result.IsSuccess);

        Assert.Contains("PFX", result.Error, StringComparison.Ordinal);

        Assert.Contains(path, result.Error, StringComparison.Ordinal);

        Assert.Contains("wrong password / unloadable certificate", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("correct", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Load_MissingFile_ReturnsSanitizedFailure()
    {

        string missing = Path.Combine(_tempRoot, "missing.pfx");

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings { CertificatePath = missing },
            secretProtector: null);

        Assert.False(result.IsSuccess);

        Assert.Contains("missing file", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("CryptographicException", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Load_PemPair_Succeeds_AndIgnoresCertificatePassword()
    {

        (string certPath, string keyPath) = CreatePemPair();

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                CertificatePassword = "should-be-ignored",
            },
            secretProtector: null);

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Certificate);

        Assert.True(result.Certificate.HasPrivateKey);

    }

    [Fact]
    public void Load_ExpiredCertificate_Fails()
    {

        (string path, string password) = CreatePfx(
            password: "expired",
            notBefore: DateTimeOffset.UtcNow.AddDays(-30),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings { CertificatePath = path, CertificatePassword = password },
            secretProtector: null);

        Assert.False(result.IsSuccess);

        Assert.Contains("expired certificate", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Configure_HttpsDisabled_DoesNotThrow()
    {

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:Https:Enabled"] = "false",
            })
            .Build();

        KestrelServerOptions options = new();

        ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: false);

        Assert.Equal(new HostSettings().MaxRequestBodyBytes, options.Limits.MaxRequestBodySize);

    }

    [Fact]
    public void Configure_HttpsEnabledWithMissingCert_ThrowsSanitized()
    {

        string missing = Path.Combine(_tempRoot, "gone.pfx");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:Https:Enabled"] = "true",
                ["Arcanum:Host:Https:Port"] = "5443",
                ["Arcanum:Host:Https:CertificatePath"] = missing,
            })
            .Build();

        KestrelServerOptions options = new();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: false));

        Assert.Contains("missing file", ex.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("CryptographicException", ex.Message, StringComparison.Ordinal);

    }

    public void Dispose()
    {

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

    private (string Path, string Password) CreatePfx(
        string? password,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {

        using RSA rsa = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));

        string effectivePassword = password ?? string.Empty;

        byte[] pfx = certificate.Export(X509ContentType.Pkcs12, string.IsNullOrEmpty(effectivePassword) ? null : effectivePassword);

        string path = Path.Combine(_tempRoot, $"test-{Guid.NewGuid():N}.pfx");

        File.WriteAllBytes(path, pfx);

        return (path, effectivePassword);

    }

    private (string CertPath, string KeyPath) CreatePemPair()
    {

        using RSA rsa = RSA.Create(2048);

        CertificateRequest request = new(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        string certPath = Path.Combine(_tempRoot, $"test-{Guid.NewGuid():N}.crt");

        string keyPath = Path.Combine(_tempRoot, $"test-{Guid.NewGuid():N}.key");

        File.WriteAllText(certPath, certificate.ExportCertificatePem());

        File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());

        return (certPath, keyPath);

    }

}
