using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

/// <summary>
/// Infrastructure-owned constants for native web-research providers.
/// </summary>
public static class WebResearchConstants
{
    public const string PerplexityProviderName =
        WebResearchProviderNames.Perplexity;

    public const string LocalHttpProviderName =
        WebResearchProviderNames.LocalHttp;

    public const string PerplexityHttpClientName = "WebResearchPerplexity";

    public const string LocalHttpClientName = "WebResearchLocalHttp";

    public const string TruncationMarker = "\n\n…(truncated)";

    public const int MaxLinkTextBytes = 512;

    public const int MaxInputQueryChars = 4_000;

    public const int MaxInputUrlChars = 4_096;

    public static readonly Uri PerplexityEndpoint = new(
        "https://api.perplexity.ai/v1/sonar",
        UriKind.Absolute);
}
