namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Settings for the in-process <c>browse_web</c> tool. Disabled by default; when enabled, the tool
/// fetches a URL, extracts the title and visible text, and returns a capped list of absolute links.
/// </summary>
public sealed record WebBrowsingSettings
{

    /// <summary>Master toggle. When <see langword="false"/> (default), the tool is not advertised.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Maximum response body bytes read from a fetched page. Default 50,000; clamped 1,000 - 1,000,000.
    /// Content beyond this is truncated with a marker.
    /// </summary>
    public int MaxContentBytes { get; init; } = 50_000;

    /// <summary>
    /// Wall-clock timeout (seconds) for the outbound HTTP request. Default 10; clamped 1 - 60.
    /// </summary>
    public int RequestTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Maximum number of links returned by the tool. Default 10; clamped 0 - 100.
    /// </summary>
    public int MaxLinks { get; init; } = 10;

}
