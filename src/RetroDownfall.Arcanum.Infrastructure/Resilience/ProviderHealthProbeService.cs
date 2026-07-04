using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Periodically probes every configured provider and feeds the result into
/// <see cref="IProviderHealthTracker"/>. Idles (1s poll of <c>Arcanum:Resilience:Enabled</c>) when
/// resilience is disabled — the default. Hot-reload friendly: reads
/// <see cref="IOptionsMonitor{TOptions}.CurrentValue"/> on every tick so newly added providers, and
/// an Enabled flip, are picked up without a restart.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService provider health probe scheduler
internal sealed class ProviderHealthProbeService(
    IOptionsMonitor<ArcanumSettings> options,
    IProviderHealthProbe probe,
    IProviderHealthTracker tracker,
    ILogger<ProviderHealthProbeService> logger) : BackgroundService
{

    private bool _wasEnabled;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {

            try
            {

                bool enabled = options.CurrentValue.Resilience?.Enabled ?? false;

                if (!enabled)
                {

                    if (_wasEnabled)
                    {

                        foreach (ProviderHealthStatus status in tracker.GetAllStatuses())
                        {
                            tracker.MarkHealthy(status.ProviderName);
                        }

                        logger.LogInformation("Resilience disabled — all provider health statuses reset.");

                    }

                    _wasEnabled = false;

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    continue;

                }

                _wasEnabled = true;

                await ProbeAllProvidersAsync(stoppingToken).ConfigureAwait(false);

                bool anyUnhealthy = tracker.GetAllStatuses().Any(static status => !status.IsHealthy);

                int intervalSeconds = anyUnhealthy
                    ? ArcanumSettingClamps.HealthRecoveryProbeIntervalSeconds(
                        options.CurrentValue.Resilience?.HealthRecoveryProbeIntervalSeconds
                            ?? new ResilienceSettings().HealthRecoveryProbeIntervalSeconds)
                    : ArcanumSettingClamps.HealthProbeIntervalSeconds(
                        options.CurrentValue.Resilience?.HealthProbeIntervalSeconds
                            ?? new ResilienceSettings().HealthProbeIntervalSeconds);

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
