namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for the Prometheus-format <c>GET /metrics</c> endpoint. Activation comes
/// from <c>Arcanum:Features:Metrics</c> and authentication policy from
/// <c>Arcanum:Security:MetricsRequireApiKey</c>.
/// </summary>
public sealed record MetricsSettings
{

    /// <summary>
    /// When <c>true</c> (default), <c>GET /metrics</c> renders Prometheus text format; when
    /// <c>false</c>, the endpoint returns <c>404</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When <c>true</c> (default), <c>GET /metrics</c> is registered with <c>ApiKeyEndpointFilter</c>
    /// (accepts <c>X-Arcanum-Key</c> or <c>Authorization: Bearer</c>). Set to <c>false</c> only to
    /// allow unauthenticated scrapes on a loopback-only bind. Forced to effectively <c>true</c> —
    /// regardless of this setting — whenever the host binds to all interfaces
    /// (<c>Arcanum:Host:ListenAny</c> / <c>ARCANUM_HOST_ANY</c>), mirroring the CORS wildcard
    /// downgrade in <c>ApiBootstrapper</c>.
    /// </summary>
    public bool RequireApiKey { get; set; } = true;

}
