using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for the in-process web-tool family. Activation comes from
/// <c>Arcanum:Features:WebBrowsing</c>, provider facts come from
/// <c>Arcanum:Integrations:WebResearch</c>, and all mechanical limits are code-owned.
/// </summary>
public sealed record WebBrowsingSettings
{

    /// <summary>
    /// Master toggle. When <see langword="false"/> (default), web tools are not advertised.
    /// </summary>
    public bool Enabled { get; set; }

    public string SearchProvider { get; set; } = WebResearchProviderNames.Perplexity;

    public string PerplexityModel { get; set; } = WebResearchModels.Sonar;

    public string? CredentialEnvironmentVariable { get; set; }

    /// <summary>Maximum accepted search-query length.</summary>
    public int MaxQueryChars { get; set; } = 4_000;

    /// <summary>Maximum accepted absolute-URL length.</summary>
    public int MaxUrlChars { get; set; } = 4_096;

    /// <summary>Maximum decompressed response bytes accepted from an upstream server.</summary>
    public int MaxResponseBytes { get; set; } = 1_000_000;

    /// <summary>
    /// Maximum model-visible UTF-8 bytes retained from an answer or fetched page. Default 50,000;
    /// clamped 1,000 - 1,000,000. Content beyond this is truncated with explicit metadata.
    /// </summary>
    public int MaxContentBytes { get; set; } = 50_000;

    /// <summary>
    /// Maximum idle interval (seconds) while connecting, waiting for headers, or reading the next
    /// body segment. Progress resets the interval. Default 15; clamped 1 - 300.
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum number of links returned by the tool. Default 10; clamped 0 - 100.
    /// </summary>
    public int MaxLinks { get; set; } = 10;

    public int MaxLinkUrlChars { get; set; } = 2_048;

    public int MaxCitations { get; set; } = 20;

    public int MaxCitationUrlChars { get; set; } = 2_048;

    public int MaxRedirects { get; set; } = 5;

}
