using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

internal sealed record PerplexityRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required PerplexityMessage[] Messages { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }
}

internal sealed record PerplexityMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

internal sealed record PerplexityResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("choices")]
    public PerplexityChoice[]? Choices { get; init; }

    [JsonPropertyName("citations")]
    public string[]? Citations { get; init; }

    [JsonPropertyName("search_results")]
    public PerplexitySearchResult[]? SearchResults { get; init; }

    [JsonPropertyName("usage")]
    public PerplexityUsage? Usage { get; init; }
}

internal sealed record PerplexityChoice
{
    [JsonPropertyName("message")]
    public PerplexityMessage? Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

internal sealed record PerplexitySearchResult
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("date")]
    public string? Date { get; init; }

    [JsonPropertyName("last_updated")]
    public string? LastUpdated { get; init; }
}

internal sealed record PerplexityUsage
{
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; init; }

    [JsonPropertyName("reasoning_tokens")]
    public long ReasoningTokens { get; init; }

    [JsonPropertyName("citation_tokens")]
    public long CitationTokens { get; init; }

    [JsonPropertyName("num_search_queries")]
    public int SearchQueries { get; init; }

    [JsonPropertyName("cost")]
    public PerplexityCost? Cost { get; init; }
}

internal sealed record PerplexityCost
{
    [JsonPropertyName("total_cost")]
    public decimal? TotalCost { get; init; }
}

internal sealed record PerplexityErrorResponse
{
    [JsonPropertyName("error")]
    public PerplexityError? Error { get; init; }
}

internal sealed record PerplexityError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}
