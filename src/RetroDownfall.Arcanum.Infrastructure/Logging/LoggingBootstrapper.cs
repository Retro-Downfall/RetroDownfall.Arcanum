using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

[ExcludeFromCodeCoverage] // Reason: Serilog host wiring glue
public static class LoggingBootstrapper
{

    /// <summary>
    /// Registers Serilog with compact JSON rolling files under the user ApplicationData folder.
    /// Hosts must call <see cref="Microsoft.Extensions.Logging.LoggingBuilderExtensions.ClearProviders"/> before infrastructure registration when replacing default loggers.
    /// </summary>
    /// <remarks>
    /// The <c>AddSerilog</c> configure callback must not resolve options or other services that
    /// demand <see cref="Microsoft.Extensions.Logging.ILogger"/> while the host is still in
    /// <c>Build()</c>. Doing so re-enters logging setup and deadlocks — WebApplicationFactory then
    /// times out waiting for HostBuilt. The ring-buffer sink is therefore deferred until first emit;
    /// file/console settings are read from <see cref="IConfiguration"/> (already built) instead of
    /// <c>IOptions&lt;ArcanumSettings&gt;</c>.
    /// </remarks>
    public static IServiceCollection AddArcanumSerilog(this IServiceCollection services)
    {

        string logDirectory = ResolveLogDirectory();

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(logDirectory);

        string logFilePath = Path.Combine(logDirectory, "arcanum-api-.json");

        InstallBootstrapStaticLogger();

        services.AddSerilog(
            (serviceProvider, loggerConfiguration) =>
            {

                LoggerConfiguration cfg = loggerConfiguration
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                    // IHttpClientFactory's default handlers log the request URI at Information under
                    // System.Net.Http.HttpClient.*. .NET redacts only the query, so a token in a path
                    // segment — the dominant hosted-MCP shape — would reach the rolling file and the ring
                    // buffer verbatim. Every named client also calls RemoveAllLoggers(); this is the
                    // pipeline-wide floor that catches the next client which forgets to.
                    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Sink(new DeferredRingBufferSink(serviceProvider));

                if (IsTesting(serviceProvider))
                {

                    return;

                }

                IConfiguration? configuration = serviceProvider.GetService<IConfiguration>();

                int retained = ArcanumSettingClamps.RetainedLogFileCount(
                    ArcanumRuntimeDefaults.RetainedLogFileCount);

                bool enableEnterpriseTelemetry = false;

                if (bool.TryParse(
                        configuration?["Arcanum:Features:EnterpriseTelemetry"],
                        out bool parsedEnterprise))
                {

                    enableEnterpriseTelemetry = parsedEnterprise;

                }

                if (enableEnterpriseTelemetry)
                {

                    cfg = cfg.WriteTo.Console(new CompactJsonFormatter());

                }

                _ = cfg.WriteTo.File(
                    new CompactJsonFormatter(),
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: retained,
                    hooks: new SecureSerilogFileHooks());

            });

        return services;

    }

    internal static string ResolveLogDirectory() => ArcanumPaths.LogDirectory;

    private static int _bootstrapStaticLoggerInstalled;

    /// <summary>
    /// Returns <see cref="Log.Logger"/> to Serilog's silent default and re-arms the one-shot
    /// bootstrap install, so a test can observe the pre-host window regardless of what an earlier
    /// test in the same process left behind.
    /// </summary>
    internal static void ResetBootstrapStaticLoggerForTests()
    {

        Log.Logger = Serilog.Core.Logger.None;

        _ = Interlocked.Exchange(ref _bootstrapStaticLoggerInstalled, 0);

    }

    /// <summary>
    /// Gives Serilog's static <see cref="Log.Logger"/> a real destination for the pre-host window.
    /// </summary>
    /// <remarks>
    /// <c>AddSerilog</c> assigns <see cref="Log.Logger"/> itself, but only when the DI logging graph
    /// is first materialized — during host <c>Build()</c>. Diagnostics emitted before that reach
    /// Serilog's silent default and vanish: <c>ArcanumMasterKeyBootstrapper</c> runs against its own
    /// throwaway container ahead of <c>Build()</c> and reports a corrupt credential store, a
    /// Grimoire/key mismatch that will abort startup, and a newly generated master key. This installs
    /// a bootstrap logger for exactly that window; <c>AddSerilog</c> replaces it at <c>Build()</c>.
    /// It writes to standard error only — the rolling JSON file must keep a single writer because
    /// Serilog's file sink is not multi-writer safe.
    /// </remarks>
    private static void InstallBootstrapStaticLogger()
    {

        if (Interlocked.Exchange(ref _bootstrapStaticLoggerInstalled, 1) == 1)
        {

            return;

        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                new CompactJsonFormatter(),
                standardErrorFromLevel: LogEventLevel.Verbose)
            .CreateLogger();

    }

    private static bool IsTesting(IServiceProvider serviceProvider)
    {

        if (string.Equals(
                Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
                "Testing",
                StringComparison.OrdinalIgnoreCase))
        {

            return true;

        }

        IHostEnvironment? env = serviceProvider.GetService<IHostEnvironment>();

        return env is not null
            && string.Equals(env.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// Resolves <see cref="SerilogLogRingBufferSink"/> on first emit so host Build can finish without
    /// re-entering the DI logging graph.
    /// </summary>
    private sealed class DeferredRingBufferSink(IServiceProvider serviceProvider) : ILogEventSink
    {

        private SerilogLogRingBufferSink? _inner;

        private readonly object _gate = new();

        public void Emit(LogEvent logEvent)
        {

            SerilogLogRingBufferSink sink = _inner ?? EnsureInner();

            sink.Emit(logEvent);

        }

        private SerilogLogRingBufferSink EnsureInner()
        {

            lock (_gate)
            {

                return _inner ??= serviceProvider.GetRequiredService<SerilogLogRingBufferSink>();

            }

        }

    }

}
