using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Periodically probes every configured provider and feeds the result into
/// <see cref="IProviderHealthTracker"/>. Provider health and fallback are automatic; the options
/// monitor is read on every tick so newly added providers are picked up without a restart.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService provider health probe scheduler
internal sealed class ProviderHealthProbeService(
    IOptionsMonitor<ArcanumSettings> options,
    IProviderHealthProbe probe,
    IProviderHealthTracker tracker,
    ILogger<ProviderHealthProbeService> logger) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {

            try
            {

                await ProbeAllProvidersAsync(stoppingToken).ConfigureAwait(false);

                bool anyUnhealthy = tracker.GetAllStatuses().Any(static status => !status.IsHealthy);
                ResilienceSettings defaults = ArcanumRuntimeDefaults.Resilience;

                int intervalSeconds = anyUnhealthy
                    ? ArcanumSettingClamps.HealthRecoveryProbeIntervalSeconds(
                        defaults.HealthRecoveryProbeIntervalSeconds)
                    : ArcanumSettingClamps.HealthProbeIntervalSeconds(
                        defaults.HealthProbeIntervalSeconds);

                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                break;

            }
            catch (Exception ex)
            {

                logger.LogError(ex, "Provider health probe scheduler tick failed; continuing.");

            }

        }

    }

    private async Task ProbeAllProvidersAsync(CancellationToken stoppingToken)
    {

        ProviderSettings[] providers = options.CurrentValue.Providers ?? [];

        foreach (ProviderSettings provider in providers)
        {

            try
            {

                bool healthy = await probe.ProbeAsync(provider, stoppingToken).ConfigureAwait(false);

                if (healthy)
                {
                    tracker.MarkHealthy(provider.Name);
                }
                else
                {
                    tracker.MarkFailed(provider.Name);
                }

            }
            catch (Exception ex)
            {

                logger.LogDebug(ex, "Health probe for provider {ProviderName} failed unexpectedly.", provider.Name);

                tracker.MarkFailed(provider.Name);

            }

        }

    }

}
