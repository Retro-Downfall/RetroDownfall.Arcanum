using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: startup-only configuration clamp diagnostics.
public sealed class ArcanumSettingsClampStartupLogger(
    IOptions<ArcanumSettings> options,
    ILogger<ArcanumSettingsClampStartupLogger> logger) : IHostedService
{

    public Task StartAsync(CancellationToken cancellationToken)
    {

        ArcanumSettings settings = options.Value;

        LogIfClamped("Arcanum:Host:Port", settings.Host.Port, ArcanumSettingClamps.HostPort(settings.Host.Port));

        EventBusSettings eventBus = settings.ResolveEventBus();

        LogIfClamped(
            "Arcanum:Execution:MaxSseConnections",
            eventBus.MaxSseConnections,
            ArcanumSettingClamps.MaxSseConnections(eventBus.MaxSseConnections));

        LogIfClamped(
            "Arcanum:Execution:MaxSseConnectionsPerType",
            eventBus.MaxSseConnectionsPerType,
            ArcanumSettingClamps.SseConnectionsPerType(eventBus.MaxSseConnectionsPerType));

        return Task.CompletedTask;

    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void LogIfClamped<T>(string key, T configured, T effective) where T : IEquatable<T>
    {

        if (configured.Equals(effective))
        {

            return;

        }

        logger.LogWarning(
            "Configuration clamp applied for {Key}: configured {Configured} → effective {Effective}",
            key,
            configured,
            effective);

    }

}
