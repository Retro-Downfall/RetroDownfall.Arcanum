using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Resilience;

namespace RetroDownfall.Arcanum.Infrastructure.Resilience;

/// <summary>
/// Thread-safe, in-memory <see cref="IProviderHealthTracker"/>. Keyed by
/// <see cref="ProviderSettings.Name"/> (case-sensitive, ordinal). Providers not yet observed are
/// assumed healthy. State resets on host restart — no Grimoire persistence.
/// </summary>
internal sealed class ProviderHealthTracker(
    ILogger<ProviderHealthTracker> logger) : IProviderHealthTracker
{

    private readonly ConcurrentDictionary<string, ProviderHealthStatus> _statuses = new(StringComparer.Ordinal);

    public event Action<ProviderHealthStatus>? HealthChanged;

    public bool IsHealthy(string providerName) =>
        !_statuses.TryGetValue(providerName, out ProviderHealthStatus? status) || status.IsHealthy;

    public void MarkFailed(string providerName)
    {

        int threshold = ArcanumSettingClamps.HealthFailureThreshold(
            ArcanumRuntimeDefaults.Resilience.HealthFailureThreshold);

        bool transitioned = false;

        ProviderHealthStatus updated = _statuses.AddOrUpdate(
            providerName,
            addValueFactory: name =>
            {
                bool isHealthy = 1 < threshold;

                transitioned = !isHealthy;

                return new ProviderHealthStatus(name, isHealthy, DateTimeOffset.UtcNow, 1);
            },
            updateValueFactory: (_, existing) =>
            {
                int failures = existing.ConsecutiveFailures + 1;

                bool isHealthy = failures < threshold;

                transitioned = existing.IsHealthy != isHealthy;

                return existing with { IsHealthy = isHealthy, LastChecked = DateTimeOffset.UtcNow, ConsecutiveFailures = failures };
            });

        if (transitioned)
        {

            logger.LogInformation(
                "Provider {ProviderName} health changed to {Status} after {ConsecutiveFailures} consecutive failure(s).",
                providerName,
                updated.IsHealthy ? "Healthy" : "Unhealthy",
                updated.ConsecutiveFailures);

            HealthChanged?.Invoke(updated);

        }

    }

    public void MarkHealthy(string providerName)
    {

        bool transitioned = false;

        ProviderHealthStatus updated = _statuses.AddOrUpdate(
            providerName,
            addValueFactory: name => new ProviderHealthStatus(name, true, DateTimeOffset.UtcNow, 0),
            updateValueFactory: (_, existing) =>
            {
                transitioned = !existing.IsHealthy;

                return existing with { IsHealthy = true, LastChecked = DateTimeOffset.UtcNow, ConsecutiveFailures = 0 };
            });

        if (transitioned)
        {

            logger.LogInformation("Provider {ProviderName} health changed to Healthy.", providerName);

            HealthChanged?.Invoke(updated);

        }

    }

    public IReadOnlyList<ProviderHealthStatus> GetAllStatuses() => _statuses.Values.ToArray();

}
