using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence.WebResearch;

/// <summary>
/// Native web-research provider capability. Providers may implement search, direct URL reading,
/// or both.
/// </summary>
[Flags]
public enum WebResearchCapabilities
{
    None = 0,
    Search = 1,
    ReadUrl = 2,
}

/// <summary>Stable provider identifiers used by configuration and dependency injection.</summary>
public static class WebResearchProviderNames
{
    public const string Perplexity = "perplexity";

    public const string LocalHttp = "local-http";
}

/// <summary>
/// Provider-neutral contract behind the native <c>web_search</c> and <c>read_url</c> tools.
/// Expected remote and content failures are returned as failed <see cref="Result{T}"/> values;
/// caller cancellation is propagated.
/// </summary>
public interface IWebResearchProvider
{
    string ProviderName { get; }

    WebResearchCapabilities Capabilities { get; }

    Task<Result<WebSearchResult>> SearchAsync(
        string query,
        WebSearchOptions options,
        CancellationToken cancellationToken = default);

    Task<Result<WebReadResult>> ReadUrlAsync(
        string url,
        WebReadOptions options,
        CancellationToken cancellationToken = default);
}
