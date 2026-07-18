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
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: long-running Kestrel host entrypoint; config readers are covered via internal static unit tests.
public sealed class ServeCommand(IThemePalette themePalette)
{

    /// <summary>
    /// Hosts the Arcanum Minimal API (default http://localhost:5001/; set Arcanum:Host:Port in arcanum.json).
    /// When ListenAny / ARCANUM_HOST_ANY is effective, binds HTTPS-only on Arcanum:Host:Https:Port.
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

                if (!AnsiConsole.Confirm(ListenAnySecurityPolicy.InteractiveConfirmPrompt, defaultValue: false))
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
                bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(ReadConfiguredListenAny(ctx.Configuration));

                ArcanumKestrelConfigurator.Configure(options, ctx.Configuration, listenAny);
            });

        builder.Logging.ClearProviders();

        builder.Services.AddArcanumApiServices(builder.Configuration);

        bool autoLaunched = IsAutoLaunched();

        if (autoLaunched)
        {
            RedirectConsoleToBootstrapLog();
        }

        if (await ArcanumMasterKeyBootstrapper.EnsureMasterApiKeyExistsAsync(cancellationToken).ConfigureAwait(false) is string newApiKey)
        {

            if (autoLaunched)
            {
                AnsiConsole.MarkupLine(
                    themePalette.HighlightMarkup(
                        Markup.Escape("Master API key generated. Retrieve it with 'arcanum key show'.")));
            }
            else
            {
                AnsiConsole.WriteLine(newApiKey);

                AnsiConsole.MarkupLine(
                    themePalette.HighlightMarkup(
                        Markup.Escape("New Master API Key generated and secured. Save this key — it will not be shown again.")));
            }

        }

        WebApplication app = builder.Build();

        bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(ReadConfiguredListenAny(builder.Configuration));

        string listenScheme = listenAny ? "https" : "http";

        string listenHost = listenAny ? "0.0.0.0" : "127.0.0.1";

        int listenPort = listenAny
            ? ArcanumSettingClamps.HostHttpsPort(ReadConfiguredHttpsPort(builder.Configuration))
            : ArcanumSettingClamps.HostPort(ReadConfiguredHostPort(builder.Configuration));

        Log.Information(
            "{Timestamp:o} Arcanum API host configured for {Scheme}://{ListenHost}:{Port}",
            DateTimeOffset.UtcNow,
            listenScheme,
            listenHost,
            listenPort);

        app.UseArcanumExceptionHandler();

        app.UseArcanumResponseCompression();

        app.UseArcanumCors();

        app.UseArcanumRateLimiter();

        app.UseArcanumMetrics();

        app.MapArcanumEndpoints();

        CancellationTokenRegistration stopRegistration = cancellationToken.Register(
            static state => ((IHostApplicationLifetime)state!).StopApplication(),
            app.Lifetime);

        Log.Information(
            "{Timestamp:o} Arcanum listening on {Scheme}://{ListenHost}:{Port}",
            DateTimeOffset.UtcNow,
            listenScheme,
            listenHost,
            listenPort);

        if (!autoLaunched)
        {
            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape($"Listening on {listenScheme}://{listenHost}:{listenPort}")));
        }

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

    internal static int ReadConfiguredHttpsPort(IConfiguration configuration)
    {
        string? raw = configuration["Arcanum:Host:Https:Port"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HttpsSettings().Port;
        }

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : new HttpsSettings().Port;
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

    internal static bool IsAutoLaunched()
    {
        string? env = Environment.GetEnvironmentVariable(ArcanumServeLauncher.AutoLaunchedEnvVar);

        if (string.IsNullOrWhiteSpace(env))
        {
            return false;
        }

        return string.Equals(env.Trim(), "1", StringComparison.Ordinal)
            || string.Equals(env.Trim(), bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Redirects Console.Out/Error to an owner-only bootstrap log so stray Console.WriteLine
    /// from libraries does not vanish. Does not add a second Serilog sink.
    /// </summary>
    internal static void RedirectConsoleToBootstrapLog()
    {
        string logPath = ArcanumServeLauncher.BootstrapLogPath;

        string? directory = Path.GetDirectoryName(logPath);

        if (!string.IsNullOrEmpty(directory))
        {
            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);
        }

        StreamWriter writer = new(new FileStream(
            logPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite))
        {
            AutoFlush = true,
        };

        Console.SetOut(writer);

        Console.SetError(writer);

        SecureFilePermissions.ApplyOwnerOnlyFile(logPath);
    }

}
