using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Api.Hosting;

/// <summary>
/// Single source of truth for Kestrel listener configuration shared by the CLI <c>serve</c> host and
/// the dev host. Sets the global request-body limit exactly once, configures the plaintext HTTP
/// listener, and — only when <c>Arcanum:Host:Https:Enabled</c> is true — adds a second TLS listener on
/// the configured HTTPS port. Reads configuration through string keys (no reflection binding) to keep
/// the Native AOT host trim-safe. When HTTPS is enabled and the certificate cannot be loaded, startup
/// fails with a sanitized, password-free message.
/// </summary>
public static class ArcanumKestrelConfigurator
{

    public static void Configure(KestrelServerOptions options, IConfiguration configuration, bool listenAny)
    {

        options.Limits.MaxRequestBodySize = ArcanumSettingClamps.MaxRequestBodyBytes(
            ReadLong(configuration, "Arcanum:Host:MaxRequestBodyBytes", new HostSettings().MaxRequestBodyBytes));

        int httpPort = ArcanumSettingClamps.HostPort(
            ReadInt(configuration, "Arcanum:Host:Port", new HostSettings().Port));

        ConfigureHttp(options, httpPort, listenAny);

        ConfigureHttpsIfEnabled(options, configuration, listenAny);

    }

    private static void ConfigureHttp(KestrelServerOptions options, int port, bool listenAny)
    {

        if (listenAny)
        {

            options.ListenAnyIP(port);

        }
        else
        {

            options.ListenLocalhost(port);

        }

    }

    private static void ConfigureHttpsIfEnabled(KestrelServerOptions options, IConfiguration configuration, bool listenAny)
    {

        HttpsSettings https = ReadHttpsSettings(configuration);

        if (!https.Enabled)
        {

            return;

        }

        int httpsPort = ArcanumSettingClamps.HostHttpsPort(https.Port);

        ConfigurationSecretProtector? protector = options.ApplicationServices is { } services
            ? services.GetService<ConfigurationSecretProtector>()
            : null;

        HttpsCertificateLoadResult result = HttpsCertificateLoader.Load(https, protector);

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

            CertificatePassword = configuration["Arcanum:Host:Https:CertificatePassword"],

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

    private static long ReadLong(IConfiguration configuration, string key, long fallback)
    {

        string? raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {

            return fallback;

        }

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : fallback;

    }

}
