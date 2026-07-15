using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Configuration;
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

        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "arcanum",
            "logs");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(logDirectory);

        string logFilePath = Path.Combine(logDirectory, "arcanum-api-.json");

        services.AddSerilog(
            (serviceProvider, loggerConfiguration) =>
            {

                LoggerConfiguration cfg = loggerConfiguration
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                    .Enrich.FromLogContext()
                    .WriteTo.Sink(new DeferredRingBufferSink(serviceProvider));

                if (IsTesting(serviceProvider))
                {

                    return;

                }

                IConfiguration? configuration = serviceProvider.GetService<IConfiguration>();

                int retainedRaw = new HostSettings().RetainedLogFileCount;

                if (int.TryParse(
                        configuration?["Arcanum:Host:RetainedLogFileCount"],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedRetained))
                {

                    retainedRaw = parsedRetained;

                }

                int retained = ArcanumSettingClamps.RetainedLogFileCount(retainedRaw);

                bool enableEnterpriseTelemetry = new HostSettings().EnableEnterpriseTelemetry;

                if (bool.TryParse(
                        configuration?["Arcanum:Host:EnableEnterpriseTelemetry"],
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
