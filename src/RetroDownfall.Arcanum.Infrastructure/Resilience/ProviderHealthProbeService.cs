using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Resilience;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Periodically probes every configured provider and feeds the result into
/// <see cref="IProviderHealthTracker"/>. Provider health and fallback are automatic; the options
/// monitor is read on every tick so newly added providers are picked up without a restart.
/// Each provider observation holds one maintenance frontier from immediately before network I/O
/// through publication to the tracker; a denied frontier preserves the prior observation.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService provider health probe scheduler
internal sealed class ProviderHealthProbeService(
    IOptionsMonitor<ArcanumSettings> options,
    IProviderHealthProbe probe,
    IProviderHealthTracker tracker,
    IGrimoireConnectionAdmissionGate admissionGate,
    ILogger<ProviderHealthProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Defaults true (the longer, "avoid hammering a down provider" interval) so a tick that
            // throws before ever reaching the tracker still backs off like an unhealthy pass, rather
            // than the delay below being skipped entirely and the loop spinning hot.
            bool anyUnhealthy = true;

            try
            {
                await ProbeAllProvidersAsync(stoppingToken).ConfigureAwait(false);

                anyUnhealthy = tracker.GetAllStatuses().Any(static status => !status.IsHealthy);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provider health probe scheduler tick failed; continuing.");
            }

            try
            {
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
        }
    }

    /// <summary>
    /// Publishes one complete, independently admitted observation for each configured provider.
    /// </summary>
    internal async Task ProbeAllProvidersAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        ProviderSettings[] providers = options.CurrentValue.Providers ?? [];

        foreach (ProviderSettings provider in providers)
        {
            stoppingToken.ThrowIfCancellationRequested();

            long observedGeneration = admissionGate.CurrentGeneration;

            if (!admissionGate.TryAcquireWorkLease(
                    GrimoireWorkKind.ProviderHealthProbe,
                    out IGrimoireWorkLease? admitted))
            {
                return;
            }

            await using IGrimoireWorkLease lease = admitted!;

            System.Diagnostics.Debug.Assert(
                lease.Generation >= observedGeneration,
                "An admitted provider-health lease cannot predate the observed generation.");

            stoppingToken.ThrowIfCancellationRequested();

            if (!lease.TryBeginExternalEffectGroup(
                    out IGrimoireExternalEffectGroup? effectGroup))
            {
                return;
            }

            await using IGrimoireExternalEffectGroup effect = effectGroup!;

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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Health probe for provider {ProviderName} failed unexpectedly.", provider.Name);

                tracker.MarkFailed(provider.Name);
            }
        }
    }
}
