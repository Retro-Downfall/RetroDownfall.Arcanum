namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Code-owned provider-resilience mechanics — periodic health probing and fallback resolution
/// across configured providers.
/// </summary>
public sealed record ResilienceSettings
{

    /// <summary>
    /// Internal runtime switch for
    /// <see cref="RetroDownfall.Arcanum.Core.Resilience.IProviderHealthTracker"/> probing and fallback.
    /// The retained public projection enables it automatically; there is no operator setting.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Interval in seconds between health probes for providers currently considered healthy. Default
    /// <c>30</c>; clamped 5–600 at runtime.
    /// </summary>
    public int HealthProbeIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Interval in seconds between health probes for providers currently marked unhealthy — slower than
    /// <see cref="HealthProbeIntervalSeconds"/> to avoid hammering a down provider. Default <c>60</c>;
    /// clamped 5–3,600 at runtime.
    /// </summary>
    public int HealthRecoveryProbeIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Number of consecutive probe or inference failures before a provider is marked Unhealthy and
    /// excluded from fallback candidates. Default <c>3</c>; clamped 1–100 at runtime.
    /// </summary>
    public int HealthFailureThreshold { get; set; } = 3;

    /// <summary>
    /// HTTP timeout in seconds for each individual health probe call. Default <c>5</c>; clamped 1–30 at
    /// runtime.
    /// </summary>
    public int HealthProbeTimeoutSeconds { get; set; } = 5;

}
