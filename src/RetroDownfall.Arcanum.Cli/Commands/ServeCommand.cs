using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Hosting;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: long-running Kestrel host entrypoint; config readers are covered via internal static unit tests.
public sealed class ServeCommand(IThemePalette themePalette, ArcanumApiClient apiClient)
{

    /// <summary>
    /// Stops the running Arcanum host from anywhere (asks the API to shut itself down; requires the
    /// master API key like every other /api verb).
    /// </summary>
    public async Task<int> Quit(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Result<bool> result = await apiClient.QuitServerAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(
                        $"Could not stop the Arcanum host: {result.Error.Message} "
                        + "If no host is running there is nothing to stop.")));

            return 1;

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape("Arcanum host shutdown requested.")));

        return 0;

    }

    /// <summary>
    /// Applies the all-interfaces binding policy before Kestrel is built. Returns the exit code the
    /// process must stop with, or <see langword="null"/> when the host may start.
    /// </summary>
    internal int? EnforceListenAnyPolicy(bool requiresInteractiveConfirmation)
    {

        if (!requiresInteractiveConfirmation)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(ListenAnySecurityPolicy.SecurityBanner)));

            return null;

        }

        if (!AnsiConsole.Console.Profile.Capabilities.Interactive)
        {

            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(
                        "Refusing to bind to all interfaces: set ARCANUM_LISTEN_ANY_ACK=1 or run interactively to acknowledge the security risk.")));

            // An acknowledgement that cannot be obtained non-interactively is a configuration
            // failure (exit 2), not a generic runtime error — see the exit-code table in
            // docs/Arcanum.Command.Reference.md.
            return (int)CliExitCode.ConfigurationError;

        }

        CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(ListenAnySecurityPolicy.SecurityBanner)));

        if (!AnsiConsole.Confirm(ListenAnySecurityPolicy.InteractiveConfirmPrompt, defaultValue: false))
        {

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape("Aborted. Set Arcanum:Host:ListenAny to false or unset ARCANUM_HOST_ANY to use loopback only.")));

            return (int)CliExitCode.GenericError;

        }

        return null;

    }

    /// <summary>
    /// Hosts the Arcanum Minimal API (default http://localhost:5001/; set Arcanum:Host:Port in arcanum.json).
    /// When ListenAny / ARCANUM_HOST_ANY is effective, binds HTTPS-only on Arcanum:Host:Https:Port.
    /// </summary>
    public async Task<int> Run(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        ConfigurationManager probeConfig = new();

        probeConfig.AddArcanumConfiguration();

        bool configuredListenAny = ArcanumEnvironment.IsHostAnyEnabled(
            ReadConfiguredListenAny(probeConfig));

        bool persistListenAnyAcknowledgement = false;

        if (configuredListenAny)
        {

            bool requiresInteractiveConfirmation =
                ListenAnySecurityPolicy.RequiresInteractiveConfirmation(
                    ReadConfiguredListenAny(probeConfig));

            int? refusal = EnforceListenAnyPolicy(requiresInteractiveConfirmation);

            if (refusal is not null)
            {

                return refusal.Value;

            }

            persistListenAnyAcknowledgement = requiresInteractiveConfirmation;

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

        WebApplication app = builder.Build();

        GrimoireDatabaseHostedService databaseHost = app.Services
            .GetRequiredService<GrimoireDatabaseHostedService>();

        if (autoLaunched || persistListenAnyAcknowledgement)
        {

            databaseHost.ConfigurePostTopologyStartupAction(() =>
            {

                IDisposable? consoleRedirection = null;

                try
                {

                    if (autoLaunched)
                    {

                        consoleRedirection = RedirectConsoleToBootstrapLog();

                    }

                    if (persistListenAnyAcknowledgement)
                    {

                        ListenAnySecurityPolicy.PersistAcknowledgement();

                    }

                    return consoleRedirection;

                }
                catch
                {

                    consoleRedirection?.Dispose();

                    throw;

                }

            });

        }

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

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            string? newApiKey = databaseHost.TakeGeneratedMasterApiKey();

            if (newApiKey is not null)
            {

                if (autoLaunched)
                {
                    AnsiConsole.MarkupLine(
                        themePalette.HighlightMarkup(
                            Markup.Escape("Master API key generated. Retrieve it with 'arcanum key show'.")));
                }
                else
                {
                    // Raw stdout: the key is a credential, not presentation, and must never be wrapped
                    // at Spectre's profile width.
                    Console.Out.WriteLine(newApiKey);

                    AnsiConsole.MarkupLine(
                        themePalette.HighlightMarkup(
                            Markup.Escape("New Master API Key generated and secured. Save this key — it will not be shown again.")));
                }

            }

            IGrimoireDbReadiness readiness = app.Services.GetRequiredService<IGrimoireDbReadiness>();
            await readiness.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);

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

            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stopRegistration.DisposeAsync().ConfigureAwait(false);

            try
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Arcanum host StopAsync during serve cleanup failed.");
            }

            await app.DisposeAsync().ConfigureAwait(false);

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
    internal static IDisposable RedirectConsoleToBootstrapLog()
    {
        string logPath = ArcanumServeLauncher.BootstrapLogPath;

        string? directory = Path.GetDirectoryName(logPath);

        if (!string.IsNullOrEmpty(directory))
        {
            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);
        }

        TextWriter previousOut = Console.Out;

        TextWriter previousError = Console.Error;

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

        return new ConsoleRedirectionLease(
            previousOut,
            previousError,
            writer);
    }

    private sealed class ConsoleRedirectionLease(
        TextWriter previousOut,
        TextWriter previousError,
        StreamWriter writer) : IDisposable
    {

        private int _disposed;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {

                return;

            }

            Console.SetOut(previousOut);

            Console.SetError(previousError);

            writer.Dispose();

        }

    }

}
