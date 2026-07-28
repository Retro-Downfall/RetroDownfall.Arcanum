using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Api.Hosting;

/// <summary>
/// Single source of truth for Kestrel listener configuration shared by the CLI <c>serve</c> host and
/// the dev host. Sets the global request-body limit exactly once. Loopback mode binds plaintext HTTP
/// on <c>Arcanum:Host:Port</c> and optionally HTTPS on <c>Arcanum:Host:Https:Port</c>. All-interfaces
/// mode (<c>ListenAny</c> / <c>ARCANUM_HOST_ANY</c>) is HTTPS-only: requires
/// <c>Arcanum:Host:Https:Enabled</c> and a loadable certificate, binds only
/// <c>ListenAnyIP(HttpsPort)</c> with TLS, and never binds plaintext any-IP HTTP. Reads configuration
/// through string keys (no reflection binding) to keep the Native AOT host trim-safe. When HTTPS is
/// required or enabled and the certificate cannot be loaded, startup fails with a sanitized,
/// password-free message.
/// </summary>
public static class ArcanumKestrelConfigurator
{

    public const string ListenAnyRequiresHttpsMessage =
        "ListenAny / ARCANUM_HOST_ANY requires Arcanum:Host:Https:Enabled with a loadable certificate; plaintext any-IP HTTP is not permitted.";

    public static void Configure(KestrelServerOptions options, IConfiguration configuration, bool listenAny)
    {

        options.Limits.MaxRequestBodySize = ArcanumSettingClamps.MaxRequestBodyBytes(
            ArcanumRuntimeDefaults.HostMaxRequestBodyBytes);

        HttpsSettings https = ReadHttpsSettings(configuration);

        if (listenAny)
        {

            ConfigureListenAnyHttpsOnly(options, https);

            return;

        }

        int httpPort = ArcanumSettingClamps.HostPort(
            ReadInt(configuration, "Arcanum:Host:Port", new HostSettings().Port));

        ConfigureHttp(options, httpPort);

        ConfigureHttpsIfEnabled(options, https, listenAny: false);

    }

    private static void ConfigureListenAnyHttpsOnly(KestrelServerOptions options, HttpsSettings https)
    {

        if (!https.Enabled)
        {

            Log.Error(
                "{Timestamp:o} {Message}",
                DateTimeOffset.UtcNow,
                ListenAnyRequiresHttpsMessage);

            throw new InvalidOperationException(ListenAnyRequiresHttpsMessage);

        }

        BindHttps(options, https, listenAny: true);

    }

    private static void ConfigureHttp(KestrelServerOptions options, int port)
    {

        options.ListenLocalhost(port);

    }

    private static void ConfigureHttpsIfEnabled(KestrelServerOptions options, HttpsSettings https, bool listenAny)
    {

        if (!https.Enabled)
        {

            return;

        }

        BindHttps(options, https, listenAny);

    }

    private static void BindHttps(KestrelServerOptions options, HttpsSettings https, bool listenAny)
    {

        int httpsPort = ArcanumSettingClamps.HostHttpsPort(https.Port);

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(https);

        if (!result.IsSuccess || result.Certificate is null)
        {

            string reason = result.Error ?? "unknown error";

            Log.Error(
                "{Timestamp:o} HTTPS is enabled but the certificate could not be loaded: {Reason}",
                DateTimeOffset.UtcNow,
                reason);

            throw new InvalidOperationException(
                $"HTTPS is enabled but the certificate could not be loaded: {reason}");

        }

        if (listenAny)
        {

            options.ListenAnyIP(httpsPort, listenOptions => listenOptions.UseHttps(result.Certificate));

        }
        else
        {

            options.ListenLocalhost(httpsPort, listenOptions => listenOptions.UseHttps(result.Certificate));

        }

        Log.Information(
            "{Timestamp:o} Arcanum HTTPS listener configured on https://{ListenHost}:{Port}",
            DateTimeOffset.UtcNow,
            listenAny ? "0.0.0.0" : "localhost",
            httpsPort);

    }

    private static HttpsSettings ReadHttpsSettings(IConfiguration configuration)
    {

        HttpsSettings defaults = new();

        return new HttpsSettings
        {

            Enabled = ReadBool(configuration, "Arcanum:Host:Https:Enabled", defaults.Enabled),

            Port = ReadInt(configuration, "Arcanum:Host:Https:Port", defaults.Port),

            CertificatePath = configuration["Arcanum:Host:Https:CertificatePath"],

            PrivateKeyPath = configuration["Arcanum:Host:Https:PrivateKeyPath"],

            CertificatePasswordEnvironmentVariable =
                configuration["Arcanum:Host:Https:CertificatePasswordEnvironmentVariable"],

        };

    }

    private static bool ReadBool(IConfiguration configuration, string key, bool fallback)
    {

        string? raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {

            return fallback;

        }

        return bool.TryParse(raw.Trim(), out bool parsed) ? parsed : fallback;

    }

    private static int ReadInt(IConfiguration configuration, string key, int fallback)
    {

        string? raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {

            return fallback;

        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    }

}
