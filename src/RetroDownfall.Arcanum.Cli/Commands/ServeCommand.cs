using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ConsoleAppFramework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: long-running Kestrel host entrypoint; config readers are covered via internal static unit tests.
public sealed class ServeCommand(IThemePalette themePalette)
{

    /// <summary>
    /// Hosts the Arcanum Minimal API (default http://localhost:5001/; set Arcanum:Host:Port in arcanum.json).
    /// </summary>
    [Command("")]
    public async Task<int> Run(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        ConfigurationManager probeConfig = new();

        probeConfig.AddArcanumConfiguration();

        bool configuredListenAny = ArcanumEnvironment.IsHostAnyEnabled(
            ReadConfiguredListenAny(probeConfig));

        if (configuredListenAny)
        {

            if (ListenAnySecurityPolicy.RequiresInteractiveConfirmation(ReadConfiguredListenAny(probeConfig)))
            {

                if (!AnsiConsole.Console.Profile.Capabilities.Interactive)
                {

                    AnsiConsole.MarkupLine(
                        themePalette.ErrorMarkup(
                            Markup.Escape(
                                "Refusing to bind to all interfaces: set ARCANUM_LISTEN_ANY_ACK=1 or run interactively to acknowledge the security risk.")));

                    return 1;

                }

                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(ListenAnySecurityPolicy.SecurityBanner)));

                if (!AnsiConsole.Confirm("Bind Arcanum to all network interfaces over plaintext HTTP?", defaultValue: false))
                {

                    AnsiConsole.MarkupLine(
                        themePalette.MutedMarkup(
                            Markup.Escape("Aborted. Set Arcanum:Host:ListenAny to false or unset ARCANUM_HOST_ANY to use loopback only.")));

                    return 1;

                }

                ListenAnySecurityPolicy.PersistAcknowledgement();

            }
            else
            {

                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(ListenAnySecurityPolicy.SecurityBanner)));

            }

        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        TaskScheduler.UnobservedTaskException += static (_, e) =>
        {

            Log.Error(e.Exception, "Unobserved task exception.");

            e.SetObserved();

        };

        builder.Host.UseWindowsService(options => options.ServiceName = "ArcanumDaemon");

        builder.Host.UseSystemd();

        builder.Configuration.AddArcanumConfiguration();

        builder.WebHost.ConfigureKestrel(
            (WebHostBuilderContext ctx, KestrelServerOptions options) =>
            {
                int configuredPort = ReadConfiguredHostPort(ctx.Configuration);

                int port = ArcanumSettingClamps.HostPort(configuredPort);

                if (ArcanumEnvironment.IsHostAnyEnabled(ReadConfiguredListenAny(ctx.Configuration)))
                {
                    options.ListenAnyIP(port);
                }
                else
                {
                    options.ListenLocalhost(port);
                }

                long maxBodyBytes = ArcanumSettingClamps.MaxRequestBodyBytes(
                    ReadConfiguredMaxRequestBodyBytes(ctx.Configuration));

                options.Limits.MaxRequestBodySize = maxBodyBytes;
            });

        builder.Logging.ClearProviders();

        builder.Services.AddArcanumApiServices(builder.Configuration);

        if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync(cancellationToken).ConfigureAwait(false) is string newApiKey)
        {

            AnsiConsole.WriteLine(newApiKey);

            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape("New Master API Key generated and secured. Save this key — it will not be shown again.")));

        }

        WebApplication app = builder.Build();

        int configuredPort = ReadConfiguredHostPort(builder.Configuration);

        int listenPort = ArcanumSettingClamps.HostPort(configuredPort);

        bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(ReadConfiguredListenAny(builder.Configuration));

        string listenHost = listenAny ? "0.0.0.0" : "127.0.0.1";

        Log.Information("{Timestamp:o} Arcanum API host configured for http://{ListenHost}:{Port}", DateTimeOffset.UtcNow, listenHost, listenPort);

        app.UseArcanumExceptionHandler();

        app.UseArcanumCors();

        app.UseArcanumRateLimiter();

        app.UseArcanumMetrics();

        app.MapArcanumEndpoints();

        CancellationTokenRegistration stopRegistration = cancellationToken.Register(
            static state => ((IHostApplicationLifetime)state!).StopApplication(),
            app.Lifetime);

        Log.Information("{Timestamp:o} Arcanum listening on http://{ListenHost}:{Port}", DateTimeOffset.UtcNow, listenHost, listenPort);

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(
                Markup.Escape($"Listening on http://{listenHost}:{listenPort}")));

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            await stopRegistration.DisposeAsync().ConfigureAwait(false);

            Log.CloseAndFlush();
        }

        return 0;
    }

    internal static int ReadConfiguredHostPort(IConfiguration configuration)
    {
        string? raw = configuration["Arcanum:Host:Port"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HostSettings().Port;
        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : new HostSettings().Port;
    }

    internal static bool ReadConfiguredListenAny(IConfiguration configuration)
    {
        string? configured = configuration["Arcanum:Host:ListenAny"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        return bool.TryParse(configured.Trim(), out bool parsed) && parsed;
    }

    internal static long ReadConfiguredMaxRequestBodyBytes(IConfiguration configuration)
    {
        string? raw = configuration["Arcanum:Host:MaxRequestBodyBytes"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HostSettings().MaxRequestBodyBytes;
        }

        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : new HostSettings().MaxRequestBodyBytes;
    }
}
