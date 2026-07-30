using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence.WebResearch;

public sealed class WebResearchContractTests
{
    [Fact]
    public void Canonical_tool_names_include_web_tools_and_retain_legacy_alias()
    {
        Assert.True(ArcanumBuiltInToolNames.IsKnown(ArcanumBuiltInToolNames.WebSearch));
        Assert.True(ArcanumBuiltInToolNames.IsKnown(ArcanumBuiltInToolNames.ReadUrl));
        Assert.True(ArcanumBuiltInToolNames.IsKnown(ArcanumBuiltInToolNames.BrowseWeb));
        Assert.Equal(
            ArcanumBuiltInToolNames.ReadUrl,
            ArcanumBuiltInToolNames.Canonicalize(ArcanumBuiltInToolNames.BrowseWeb));
        Assert.False(ArcanumBuiltInToolNames.IsAttunementExempt(
            ArcanumBuiltInToolNames.WebSearch));
        Assert.False(ArcanumBuiltInToolNames.IsAttunementExempt(
            ArcanumBuiltInToolNames.ReadUrl));
    }

    [Fact]
    public async Task Provider_contract_supports_independent_capabilities_and_expected_failures()
    {
        FakeSearchProvider provider = new();

        Assert.Equal(WebResearchProviderNames.Perplexity, provider.ProviderName);
        Assert.Equal(WebResearchCapabilities.Search, provider.Capabilities);

        Result<WebSearchResult> search =
            await provider.SearchAsync("query", new WebSearchOptions());
        Result<WebReadResult> read =
            await provider.ReadUrlAsync("https://example.test", new WebReadOptions());

        Assert.True(search.IsSuccess);
        Assert.Equal("answer", search.Value.Answer);
        Assert.True(read.IsFailure);
        Assert.Equal(ErrorCodes.WebResearch.UnsupportedOperation, read.Error.Code);
    }

    [Fact]
    public void Code_owned_option_defaults_match_runtime_web_browsing_limits()
    {
        WebBrowsingSettings runtime = ArcanumRuntimeDefaults.WebBrowsing;
        WebSearchOptions search = new();
        WebReadOptions read = new();

        Assert.Equal(
            TimeSpan.FromSeconds(runtime.RequestTimeoutSeconds),
            search.Timeout);
        Assert.Equal(runtime.MaxResponseBytes, search.MaxResponseBytes);
        Assert.Equal(runtime.MaxContentBytes, search.MaxAnswerBytes);
        Assert.Equal(runtime.MaxCitations, search.MaxCitations);
        Assert.Equal(runtime.MaxCitationUrlChars, search.MaxCitationUrlChars);

        Assert.Equal(
            TimeSpan.FromSeconds(runtime.RequestTimeoutSeconds),
            read.Timeout);
        Assert.Equal(runtime.MaxResponseBytes, read.MaxResponseBytes);
        Assert.Equal(runtime.MaxContentBytes, read.MaxContentBytes);
        Assert.Equal(runtime.MaxLinks, read.MaxLinks);
        Assert.Equal(runtime.MaxLinkUrlChars, read.MaxLinkUrlChars);
        Assert.Equal(runtime.MaxRedirects, read.MaxRedirects);
    }

    [Theory]
    [InlineData("sonar")]
    [InlineData("SONAR")]
    [InlineData(" sonar-pro ")]
    public void Perplexity_model_catalog_accepts_only_supported_model_names(string model)
    {
        Assert.True(WebResearchModels.IsSupportedPerplexityModel(model));
        Assert.False(WebResearchModels.IsSupportedPerplexityModel("sonar-reasoning"));
        Assert.False(WebResearchModels.IsSupportedPerplexityModel(null));
    }

    private sealed class FakeSearchProvider : IWebResearchProvider
    {
        public string ProviderName => WebResearchProviderNames.Perplexity;

        public WebResearchCapabilities Capabilities => WebResearchCapabilities.Search;

        public Task<Result<WebSearchResult>> SearchAsync(
            string query,
            WebSearchOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<WebSearchResult>>(
                new WebSearchResult(
                    "answer",
                    [],
                    new WebResearchUsage()));

        public Task<Result<WebReadResult>> ReadUrlAsync(
            string url,
            WebReadOptions options,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<WebReadResult>.Failure(
                new Error(
                    ErrorCodes.WebResearch.UnsupportedOperation,
                    "Search-only provider.")));
    }
}
