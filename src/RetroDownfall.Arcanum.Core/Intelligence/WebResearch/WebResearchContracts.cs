namespace RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

/// <summary>Code-owned limits and provider selection for a single synthesized web search.</summary>
public sealed record WebSearchOptions
{
    public string Model { get; init; } = WebResearchModels.Sonar;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum decompressed response bytes accepted from the upstream provider.</summary>
    public int MaxResponseBytes { get; init; } = 1_000_000;

    /// <summary>Maximum UTF-8 bytes retained in the synthesized answer.</summary>
    public int MaxAnswerBytes { get; init; } = 50_000;

    public int MaxCitations { get; init; } = 20;

    public int MaxCitationUrlChars { get; init; } = 2_048;
}

/// <summary>Code-owned limits for a single direct URL read.</summary>
public sealed record WebReadOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum decompressed response bytes accepted from the remote server.</summary>
    public int MaxResponseBytes { get; init; } = 1_000_000;

    /// <summary>Maximum UTF-8 bytes retained in model-visible Markdown.</summary>
    public int MaxContentBytes { get; init; } = 50_000;

    public int MaxLinks { get; init; } = 10;

    public int MaxLinkUrlChars { get; init; } = 2_048;

    public int MaxRedirects { get; init; } = 5;
}

/// <summary>Provider-supported Perplexity models exposed through public configuration.</summary>
public static class WebResearchModels
{
    public const string Sonar = "sonar";

    public const string SonarPro = "sonar-pro";

    public static bool IsSupportedPerplexityModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        string candidate = model.Trim();

        return string.Equals(candidate, Sonar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, SonarPro, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>An ordered citation associated with a synthesized web-search answer.</summary>
public sealed record WebCitation(
    int Index,
    string Url,
    string? Title = null,
    string? PublishedDate = null);

/// <summary>A link retained from a directly fetched page.</summary>
public sealed record WebLink(
    string Text,
    string Url);

/// <summary>
/// Optional usage reported by a web-research API. Zero represents a field not reported by the
/// provider; <see cref="CostUsd"/> remains nullable so an actual zero-cost report is distinguishable
/// from absent cost data.
/// </summary>
public sealed record WebResearchUsage(
    long PromptTokens = 0,
    long CompletionTokens = 0,
    long TotalTokens = 0,
    long ReasoningTokens = 0,
    long CitationTokens = 0,
    int SearchQueries = 0,
    decimal? CostUsd = null);

/// <summary>Bounded synthesized answer and its provider-preserved citation order.</summary>
public sealed record WebSearchResult(
    string Answer,
    WebCitation[] Citations,
    WebResearchUsage Usage,
    bool Truncated = false,
    int OmittedCitationCount = 0);

/// <summary>Bounded Markdown extracted from a direct HTTP response.</summary>
public sealed record WebReadResult(
    string? Title,
    string Markdown,
    string FinalUrl,
    WebLink[] Links,
    bool Truncated = false,
    int OmittedLinkCount = 0);
