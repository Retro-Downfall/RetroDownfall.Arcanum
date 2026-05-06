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
                int retained = ArcanumSettingClamps.RetainedLogFileCount(
                    serviceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value.Host.RetainedLogFileCount);

                _ = loggerConfiguration
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        new CompactJsonFormatter(),
                        logFilePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: retained,
                        shared: true);
            });

        return services;
    }
}

