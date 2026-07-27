namespace RetroDownfall.Arcanum.Core.Resilience;

/// <summary>
/// In-memory, thread-safe registry of per-provider health, updated both reactively (inference
/// failures) and periodically (<c>ProviderHealthProbeService</c>). Providers not yet observed are
/// assumed healthy. State resets on host restart — no Grimoire persistence.
/// </summary>
public interface IProviderHealthTracker
{

    /// <summary>
    /// Returns <c>true</c> when the named provider is healthy or degraded (below the code-owned
    /// failure threshold), or when the provider has not yet been observed. Returns <c>false</c> once
    /// consecutive failures reach that threshold.
    /// </summary>
    bool IsHealthy(string providerName);

    /// <summary>
    /// Records a failed probe or inference attempt for the named provider, incrementing its
    /// consecutive-failure count. Fires <see cref="HealthChanged"/> on a healthy/unhealthy transition.
    /// </summary>
    void MarkFailed(string providerName);

    /// <summary>
    /// Records a successful probe or inference attempt for the named provider, resetting its
    /// consecutive-failure count to zero. Fires <see cref="HealthChanged"/> on a healthy/unhealthy
    /// transition.
    /// </summary>
    void MarkHealthy(string providerName);

    /// <summary>
    /// Returns a snapshot of every provider currently tracked.
    /// </summary>
    IReadOnlyList<ProviderHealthStatus> GetAllStatuses();

    /// <summary>
    /// Raised after a provider's health status changes. No subscribers today — reserved for future SSE
    /// observability.
    /// </summary>
    event Action<ProviderHealthStatus>? HealthChanged;

}
