using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Core.Resilience;

/// <summary>
/// Lightweight connectivity check for a single configured provider. Implementations must respect the
/// supplied cancellation token and never throw — connectivity failures are reported via the boolean
/// result, not exceptions.
/// </summary>
public interface IProviderHealthProbe
{

    Task<bool> ProbeAsync(ProviderSettings provider, CancellationToken cancellationToken);

}
