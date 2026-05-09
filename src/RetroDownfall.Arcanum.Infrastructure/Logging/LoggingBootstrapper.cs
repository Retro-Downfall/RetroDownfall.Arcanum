using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

public static class LoggingBootstrapper
{
    /// <summary>
    /// Registers Serilog with compact JSON rolling files under the user ApplicationData folder.
    /// Hosts must call <see cref="Microsoft.Extensions.Logging.LoggingBuilderExtensions.ClearProviders"/> before infrastructure registration when replacing default loggers.
    /// </summary>
    public static IServiceCollection AddArcanumSerilog(this IServiceCollection services)
    {
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "arcanum",
            "logs");

        Directory.CreateDirectory(logDirectory);

        string logFilePath = Path.Combine(logDirectory, "arcanum-api-.json");

        services.AddSerilog(
            (serviceProvider, loggerConfiguration) =>
            {
                ArcanumSettings arcSettings = serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

                int retained = ArcanumSettingClamps.RetainedLogFileCount(arcSettings.Host.RetainedLogFileCount);

                LoggerConfiguration cfg = loggerConfiguration
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                    .Enrich.FromLogContext();

                if (arcSettings.Host.EnableEnterpriseTelemetry)
                {
                    cfg = cfg.WriteTo.Console(new CompactJsonFormatter());
                }

                _ = cfg.WriteTo.File(
                    new CompactJsonFormatter(),
                    logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: retained,
                    shared: true);
            });

        return services;
    }
}

