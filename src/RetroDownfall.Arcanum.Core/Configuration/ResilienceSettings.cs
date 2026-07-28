namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Configuration for the provider resilience layer — periodic health probing, fallback resolution,
/// and inference retry across configured providers. Bound from <c>Arcanum:Resilience</c>.
/// </summary>
public sealed record ResilienceSettings
{

    /// <summary>
    /// When <c>true</c>, <see cref="RetroDownfall.Arcanum.Core.Resilience.IProviderHealthTracker"/> probing
    /// runs and fallback resolution is active. When <c>false</c> (default), behavior is unchanged: the
    /// probe service idles and the resolver returns exactly one candidate.
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
    /// Maximum number of candidate providers to try per inference turn before giving up. Default
    /// <c>3</c>; clamped 1–10 at runtime.
    /// </summary>
    public int MaxFallbackAttempts { get; set; } = 3;

    /// <summary>
    /// HTTP timeout in seconds for each individual health probe call. Default <c>5</c>; clamped 1–30 at
    /// runtime.
    /// </summary>
    public int HealthProbeTimeoutSeconds { get; set; } = 5;

}
