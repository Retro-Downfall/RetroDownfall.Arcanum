using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog.Core;
using Serilog.Events;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("ProcessEnvironment")]
public sealed class HttpsCertificateLoaderTests : IDisposable
{
    private const string PasswordVariable = "ARCANUM_TEST_HTTPS_PFX_PASSWORD";

    private readonly string _tempRoot;
    private readonly string? _originalPassword;

    public HttpsCertificateLoaderTests()
    {

        _tempRoot = Path.Combine(Path.GetTempPath(), $"arcanum-https-loader-{Guid.NewGuid():N}");

        _ = Directory.CreateDirectory(_tempRoot);

        _originalPassword = System.Environment.GetEnvironmentVariable(PasswordVariable);

        System.Environment.SetEnvironmentVariable(PasswordVariable, null);

    }

    [Fact]
    public void Load_PfxWithExplicitEnvironmentPassword_Succeeds()
    {

        (string path, string password) = CreatePfx(password: "test-password");

        System.Environment.SetEnvironmentVariable(PasswordVariable, password);

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings
            {
                CertificatePath = path,
                CertificatePasswordEnvironmentVariable = PasswordVariable,
            });

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Certificate);

        Assert.True(result.Certificate.HasPrivateKey);

    }

    [Fact]
    public void Load_PasswordlessPfx_Succeeds()
    {

        (string path, _) = CreatePfx(password: null);

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings
            {
                CertificatePath = path,
                CertificatePasswordEnvironmentVariable = PasswordVariable,
            });

        Assert.True(result.IsSuccess);

        Assert.NotNull(result.Certificate);

    }

    [Fact]
    public void Load_WrongPfxPassword_ReturnsSanitizedFailure()
    {

        (string path, _) = CreatePfx(password: "correct");

        System.Environment.SetEnvironmentVariable(PasswordVariable, "wrong");

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings
            {
                CertificatePath = path,
                CertificatePasswordEnvironmentVariable = PasswordVariable,
            });

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
            new HttpsSettings { CertificatePath = missing });

        Assert.False(result.IsSuccess);

        Assert.Contains("missing file", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("CryptographicException", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Load_PemPair_Succeeds_AndIgnoresCertificatePasswordEnvironment()
    {

        (string certPath, string keyPath) = CreatePemPair();

        System.Environment.SetEnvironmentVariable(
            PasswordVariable,
            "should-be-ignored");

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings
            {
                CertificatePath = certPath,
                PrivateKeyPath = keyPath,
                CertificatePasswordEnvironmentVariable = PasswordVariable,
            });

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

        System.Environment.SetEnvironmentVariable(PasswordVariable, password);

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(
            new HttpsSettings
            {
                CertificatePath = path,
                CertificatePasswordEnvironmentVariable = PasswordVariable,
            });

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

        Assert.Equal(
            ArcanumSettingClamps.MaxRequestBodyBytes(
                ArcanumRuntimeDefaults.HostMaxRequestBodyBytes),
            options.Limits.MaxRequestBodySize);

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

    [Fact]
    public void Configure_ListenAnyWithMissingCert_ThrowsSanitized()
    {

        string missing = Path.Combine(_tempRoot, "listen-any-gone.pfx");

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
            () => ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: true));

        Assert.Contains("missing file", ex.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("CryptographicException", ex.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The sanitized failure string names the file but not the cause, so the underlying
    /// <see cref="CryptographicException"/> is the only thing that tells an operator whether the PFX
    /// was locked by the wrong password, corrupt, or rejected by the platform key store. The Kestrel
    /// bind is the sole production caller of the loader, so if it does not hand the loader a logger
    /// that exception is never written anywhere.
    /// </summary>
    [Fact]
    public void Configure_HttpsEnabledWithWrongPassword_LogsUnderlyingException()
    {

        (string path, _) = CreatePfx(password: "correct");

        System.Environment.SetEnvironmentVariable(PasswordVariable, "wrong");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arcanum:Host:Https:Enabled"] = "true",
                ["Arcanum:Host:Https:Port"] = "5443",
                ["Arcanum:Host:Https:CertificatePath"] = path,
                ["Arcanum:Host:Https:CertificatePasswordEnvironmentVariable"] = PasswordVariable,
            })
            .Build();

        KestrelServerOptions options = new();

        CapturingSink sink = new();

        Serilog.ILogger previous = Serilog.Log.Logger;

        Serilog.Log.Logger = new Serilog.LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        try
        {

            _ = Assert.Throws<InvalidOperationException>(
                () => ArcanumKestrelConfigurator.Configure(options, configuration, listenAny: false));

        }
        finally
        {

            Serilog.Log.Logger = previous;

        }

        LogEvent? loadFailure = sink.Events.FirstOrDefault(logEvent => logEvent.Exception is not null);

        Assert.NotNull(loadFailure);

        Assert.Equal(LogEventLevel.Error, loadFailure.Level);

        Assert.Contains("PFX", loadFailure.MessageTemplate.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("correct", loadFailure.RenderMessage(), StringComparison.Ordinal);

    }

    public void Dispose()
    {

        System.Environment.SetEnvironmentVariable(PasswordVariable, _originalPassword);

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

    private sealed class CapturingSink : ILogEventSink
    {

        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {

            get
            {

                lock (_events)
                {

                    return [.. _events];

                }

            }

        }

        public void Emit(LogEvent logEvent)
        {

            lock (_events)
            {

                _events.Add(logEvent);

            }

        }

    }

}
