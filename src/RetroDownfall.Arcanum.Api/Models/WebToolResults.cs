namespace RetroDownfall.Arcanum.Api.Models;

/// <summary>
/// Model-facing citation returned by the native <c>web_search</c> tool.
/// </summary>
public sealed record WebToolCitation
{
    /// <summary>
    /// Provider-assigned, one-based citation index. Indices are deliberately not renumbered when a
    /// bounded result omits trailing citations.
    /// </summary>
    public int Index { get; init; }

    public string Url { get; init; } = string.Empty;

    public string? Title { get; init; }

    public string? PublishedDate { get; init; }
}

/// <summary>
/// Provider usage metadata. All fields are aggregate numbers; queries, URLs, credentials, and
/// provider response text are intentionally absent.
/// </summary>
public sealed record WebToolUsage
{
    public long PromptTokens { get; init; }

    public long CompletionTokens { get; init; }

    public long TotalTokens { get; init; }

    public long ReasoningTokens { get; init; }

    public long CitationTokens { get; init; }

    public int SearchQueries { get; init; }

    public decimal? CostUsd { get; init; }
}

public sealed record WebSearchToolData
{
    public string Answer { get; init; } = string.Empty;

    public WebToolCitation[] Citations { get; init; } = [];

    public int TotalCitationCount { get; init; }

    public int OmittedCitationCount { get; init; }
}

/// <summary>
/// Stable JSON envelope returned by <c>web_search</c>.
/// </summary>
public sealed record WebSearchToolResultEnvelope
{
    public string Status { get; init; } = "ok";

    public string? Code { get; init; }

    public string? Message { get; init; }

    public string Provider { get; init; } = string.Empty;

    public string? Model { get; init; }

    public WebSearchToolData? Data { get; init; }

    public WebToolUsage? Usage { get; init; }

    public string? SuggestedTool { get; init; }

    public bool Truncated { get; init; }
}

public sealed record WebToolLink
{
    public string Text { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;
}

public sealed record ReadUrlToolData
{
    public string? Title { get; init; }

    public string Markdown { get; init; } = string.Empty;

    public string FinalUrl { get; init; } = string.Empty;

    public WebToolLink[] Links { get; init; } = [];

    public int TotalLinkCount { get; init; }

    public int OmittedLinkCount { get; init; }
}

/// <summary>
/// Stable JSON envelope returned by <c>read_url</c>.
/// </summary>
public sealed record ReadUrlToolResultEnvelope
{
    public string Status { get; init; } = "ok";

    public string? Code { get; init; }

    public string? Message { get; init; }

    public string Provider { get; init; } = string.Empty;

    public ReadUrlToolData? Data { get; init; }

    public WebToolUsage? Usage { get; init; }

    public string? SuggestedTool { get; init; }

    public bool Truncated { get; init; }
}
