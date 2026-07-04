namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Configuration for the Prometheus-format <c>GET /metrics</c> endpoint. Bound from
/// <c>Arcanum:Metrics</c>. No <see cref="ArcanumSettingClamps"/> entries are needed — both values are
/// booleans with no numeric range to clamp.
/// </summary>
public sealed record MetricsSettings
{

    /// <summary>
    /// When <c>true</c> (default), <c>GET /metrics</c> renders Prometheus text format; when
    /// <c>false</c>, the endpoint returns <c>404</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, <c>/metrics</c> is mapped behind <c>ApiKeyEndpointFilter</c> instead of as a
    /// standalone unauthenticated route. Default <c>false</c> (loopback-only deployments do not need
    /// header-based auth for a scrape target). This is forced to effectively <c>true</c> — regardless of
    /// this setting — whenever the host binds to all interfaces (<c>Arcanum:Host:ListenAny</c> /
    /// <c>ARCANUM_HOST_ANY</c>), mirroring the CORS wildcard downgrade in <c>ApiBootstrapper</c>.
    /// </summary>
    public bool RequireApiKey { get; init; }

}
