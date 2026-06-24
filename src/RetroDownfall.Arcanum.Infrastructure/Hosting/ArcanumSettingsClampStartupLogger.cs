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

        LogIfClamped(
            "Arcanum:Host:MaxRequestBodyBytes",
            settings.Host.MaxRequestBodyBytes,
            ArcanumSettingClamps.MaxRequestBodyBytes(settings.Host.MaxRequestBodyBytes));

        EventBusSettings eventBus = settings.EventBus ?? new EventBusSettings();

        LogIfClamped(
            "Arcanum:EventBus:ChannelCapacity",
            eventBus.ChannelCapacity,
            ArcanumSettingClamps.EventBusChannelCapacity(eventBus.ChannelCapacity));

        LogIfClamped(
            "Arcanum:EventBus:HeartbeatSeconds",
            eventBus.HeartbeatSeconds,
            ArcanumSettingClamps.EventBusHeartbeatSeconds(eventBus.HeartbeatSeconds));

        IntelligenceSettings intelligence = settings.Intelligence ?? new IntelligenceSettings();

        LogIfClamped(
            "Arcanum:Intelligence:MaxToolInferenceRounds",
            intelligence.MaxToolInferenceRounds,
            ArcanumSettingClamps.MaxToolInferenceRounds(intelligence.MaxToolInferenceRounds));

        LogIfClamped(
            "Arcanum:Intelligence:InferenceTimeoutSeconds",
            intelligence.InferenceTimeoutSeconds,
            ArcanumSettingClamps.InferenceTimeoutSeconds(intelligence.InferenceTimeoutSeconds));

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
