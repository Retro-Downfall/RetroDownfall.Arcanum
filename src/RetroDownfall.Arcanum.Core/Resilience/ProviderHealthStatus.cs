namespace RetroDownfall.Arcanum.Core.Resilience;

/// <summary>
/// Point-in-time health snapshot for a configured provider, keyed by
/// <see cref="RetroDownfall.Arcanum.Core.Configuration.ProviderSettings.Name"/>. In-memory only — never
/// persisted to the Grimoire.
/// </summary>
public sealed record ProviderHealthStatus(
    string ProviderName,
    bool IsHealthy,
    DateTimeOffset LastChecked,
    int ConsecutiveFailures);
