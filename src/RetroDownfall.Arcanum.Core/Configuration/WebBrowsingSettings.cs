namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for the in-process <c>browse_web</c> tool. Activation comes from
/// <c>Arcanum:Features:WebBrowsing</c>; fetch limits are code-owned.
/// </summary>
public sealed record WebBrowsingSettings
{

    /// <summary>Master toggle. When <see langword="false"/> (default), the tool is not advertised.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum response body bytes read from a fetched page. Default 50,000; clamped 1,000 - 1,000,000.
    /// Content beyond this is truncated with a marker.
    /// </summary>
    public int MaxContentBytes { get; set; } = 50_000;

    /// <summary>
    /// Wall-clock timeout (seconds) for the outbound HTTP request. Default 10; clamped 1 - 60.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of links returned by the tool. Default 10; clamped 0 - 100.
    /// </summary>
    public int MaxLinks { get; set; } = 10;

}
