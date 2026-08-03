using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.Tools;

public sealed class ArcanumNativeWebToolTests
{
    [Fact]
    public void Schemas_AreStrictAndExposeOnlyRequiredArguments()
    {
        StubProvider searchProvider = CreateSearchProvider(
            static (_, _, _) => Task.FromResult(
                Result<WebSearchResult>.Failure(
                    new Error(
                        ErrorCodes.WebResearch.ProviderUnavailable,
                        "Unavailable."))));
        StubProvider readProvider = CreateReadProvider(
            static (_, _, _) => Task.FromResult(
                Result<WebReadResult>.Failure(
                    new Error(
                        ErrorCodes.WebResearch.ProviderUnavailable,
                        "Unavailable."))));
        StubCatalog catalog = new(searchProvider, readProvider);

        ArcanumWebSearchTool search = new(
            catalog,
            Settings(),
            NullLogger.Instance);
        ArcanumReadUrlTool read = new(
            catalog,
            Settings(),
            NullLogger.Instance);

        Assert.False(
            search.JsonSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["query"],
            search.JsonSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray());
        Assert.Equal(
            ["query"],
            search.JsonSchema
                .GetProperty("properties")
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());

        Assert.False(
            read.JsonSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["url"],
            read.JsonSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray());
        Assert.Equal(
            ["url"],
            read.JsonSchema
                .GetProperty("properties")
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task WebSearch_ReturnsFramedAnswerOrderedCitationIndicesAndUsage()
    {
        WebSearchOptions? observedOptions = null;
        StubProvider provider = CreateSearchProvider(
            (query, options, _) =>
            {
                Assert.Equal("current facts", query);
                observedOptions = options;

                return Task.FromResult(
                    Result<WebSearchResult>.Success(
                        new WebSearchResult(
                            "Answer text.",
                            [
                                new WebCitation(7, "https://example.test/7", "Seven"),
                                new WebCitation(12, "https://example.test/12", "Twelve"),
                            ],
                            new WebResearchUsage(
                                PromptTokens: 3,
                                CompletionTokens: 5,
                                TotalTokens: 8,
                                SearchQueries: 2,
                                CostUsd: 0.001m))));
            });
        ArcanumWebSearchTool tool = new(
            new StubCatalog(provider),
            Settings(model: WebResearchModels.SonarPro),
            NullLogger.Instance);

        object? raw = await tool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["query"] = " current facts ",
                }),
            CancellationToken.None);

        using JsonDocument document = JsonDocument.Parse(
            Assert.IsType<string>(raw));
        JsonElement root = document.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(
            WebResearchModels.SonarPro,
            root.GetProperty("model").GetString());
        Assert.StartsWith(
            ArcanumWebSearchTool.UntrustedAnswerFraming,
            root.GetProperty("data").GetProperty("answer").GetString());
        Assert.Equal(
            [7, 12],
            root.GetProperty("data")
                .GetProperty("citations")
                .EnumerateArray()
                .Select(static citation => citation.GetProperty("index").GetInt32())
                .ToArray());
        Assert.Equal(
            8,
            root.GetProperty("usage").GetProperty("totalTokens").GetInt64());
        Assert.NotNull(observedOptions);
        Assert.Equal(WebResearchModels.SonarPro, observedOptions.Model);
        Assert.Equal(TimeSpan.FromSeconds(15), observedOptions.IdleTimeout);
    }

    [Fact]
    public async Task ReadUrl_BotProtectionReturnsSafeFallbackSuggestion()
    {
        StubProvider provider = CreateReadProvider(
            static (_, _, _) => Task.FromResult(
                Result<WebReadResult>.Failure(
                    new Error(
                        ErrorCodes.WebResearch.BotProtected,
                        "raw provider response containing a secret"))));
        ArcanumReadUrlTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        object? raw = await tool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["url"] = "https://example.test/article",
                }),
            CancellationToken.None);

        string json = Assert.IsType<string>(raw);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(
            ErrorCodes.WebResearch.BotProtected,
            root.GetProperty("code").GetString());
        Assert.Equal(
            ArcanumWebSearchTool.ToolName,
            root.GetProperty("suggestedTool").GetString());
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebSearch_IdleTimeoutNamesTheBoundaryOwnerSavedStateAndRecovery()
    {

        StubProvider provider = CreateSearchProvider(
            static (_, _, _) => Task.FromResult(
                Result<WebSearchResult>.Failure(
                    new Error(
                        ErrorCodes.WebResearch.Timeout,
                        "raw transport detail"))));

        ArcanumWebSearchTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        string json = Assert.IsType<string>(
            await tool.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["query"] = "test",
                    }),
                CancellationToken.None));

        using JsonDocument document = JsonDocument.Parse(json);

        string message = document.RootElement.GetProperty("message").GetString()!;

        Assert.Contains("provider/transport timeout boundary", message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("No partial provider response was saved", message, StringComparison.Ordinal);

        Assert.Contains("Retry", message, StringComparison.Ordinal);

        Assert.DoesNotContain("raw transport detail", json, StringComparison.Ordinal);

    }

    [Fact]
    public async Task WebSearch_UnexpectedFaultReturnsSanitizedStructuredError()
    {
        StubProvider provider = CreateSearchProvider(
            static (_, _, _) => throw new InvalidOperationException(
                "credential and raw response"));
        ArcanumWebSearchTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        object? raw = await tool.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["query"] = "test",
                }),
            CancellationToken.None);

        string json = Assert.IsType<string>(raw);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(
            WebToolAdapterHelpers.InternalErrorCode,
            root.GetProperty("code").GetString());
        Assert.Equal(
            ToolExecutionPipeline.PublicToolFailureMessage(
                ArcanumWebSearchTool.ToolName),
            root.GetProperty("message").GetString());
        Assert.DoesNotContain(
            "credential",
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebSearch_LargeMultibyteResultRemainsValidJsonWithinMaterializerCeiling()
    {
        WebCitation[] citations = Enumerable.Range(1, 60)
            .Select(
                static index => new WebCitation(
                    index * 3,
                    $"https://example.test/{index}/"
                    + new string('u', 500),
                    new string('t', 300)))
            .ToArray();
        StubProvider provider = CreateSearchProvider(
            (_, _, _) => Task.FromResult(
                Result<WebSearchResult>.Success(
                    new WebSearchResult(
                        string.Concat(
                            Enumerable.Repeat("answer 🔮 ", 8_000)),
                        citations,
                        new WebResearchUsage()))));
        ArcanumWebSearchTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        string json = Assert.IsType<string>(
            await tool.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["query"] = "bounded",
                    }),
                CancellationToken.None));

        Assert.True(
            Encoding.UTF8.GetByteCount(json)
            <= WebToolResultSerializer.MaxUtf8Bytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(
            root.GetProperty("data")
                .GetProperty("omittedCitationCount")
                .GetInt32()
            > 0);
        JsonElement.ArrayEnumerator retainedCitations = root
            .GetProperty("data")
            .GetProperty("citations")
            .EnumerateArray();
        Assert.True(retainedCitations.MoveNext());
        Assert.Equal(3, retainedCitations.Current.GetProperty("index").GetInt32());
    }

    [Fact]
    public async Task WebSearch_PathologicalCitationEscapingKeepsUsefulAnswerEnvelope()
    {
        StubProvider provider = CreateSearchProvider(
            (_, _, _) => Task.FromResult(
                Result<WebSearchResult>.Success(
                    new WebSearchResult(
                        "Useful answer.",
                        [
                            new WebCitation(
                                1,
                                new string('\u0001', 2_048),
                                new string('\u0002', 512)),
                        ],
                        new WebResearchUsage()))));
        ArcanumWebSearchTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        string json = Assert.IsType<string>(
            await tool.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["query"] = "bounded",
                    }),
                CancellationToken.None));

        Assert.True(
            Encoding.UTF8.GetByteCount(json)
            <= WebToolResultSerializer.MaxUtf8Bytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Contains(
            "Useful answer.",
            root.GetProperty("data").GetProperty("answer").GetString());
        Assert.Empty(
            root.GetProperty("data")
                .GetProperty("citations")
                .EnumerateArray());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ReadUrl_CallerCancellationPropagates()
    {
        StubProvider provider = CreateReadProvider(
            static async (_, _, cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);

                throw new InvalidOperationException("Unreachable.");
            });
        ArcanumReadUrlTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tool.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["url"] = "https://example.test/",
                    }),
                cancellation.Token));
    }

    [Fact]
    public async Task ReadUrl_LargeResultRetainsAtLeastOneLinkWithinMaterializerCeiling()
    {
        WebLink[] links = Enumerable.Range(1, 60)
            .Select(
                static index => new WebLink(
                    $"Link {index}",
                    $"https://example.test/{index}/" + new string('u', 500)))
            .ToArray();
        StubProvider provider = CreateReadProvider(
            (_, _, _) => Task.FromResult(
                Result<WebReadResult>.Success(
                    new WebReadResult(
                        "Large page",
                        string.Concat(Enumerable.Repeat("page 🔮 ", 8_000)),
                        "https://example.test/final",
                        links))));
        ArcanumReadUrlTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        string json = Assert.IsType<string>(
            await tool.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["url"] = "https://example.test/start",
                    }),
                CancellationToken.None));

        Assert.True(
            Encoding.UTF8.GetByteCount(json)
            <= WebToolResultSerializer.MaxUtf8Bytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement.ArrayEnumerator retainedLinks = root
            .GetProperty("data")
            .GetProperty("links")
            .EnumerateArray();

        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(retainedLinks.MoveNext());
        Assert.Equal(
            "https://example.test/1/" + new string('u', 500),
            retainedLinks.Current.GetProperty("url").GetString());
    }

    [Fact]
    public async Task ReadUrl_PathologicalLinkEscapingKeepsUsefulPageEnvelope()
    {
        StubProvider provider = CreateReadProvider(
            (_, _, _) => Task.FromResult(
                Result<WebReadResult>.Success(
                    new WebReadResult(
                        "Page",
                        "Useful page.",
                        "https://example.test/final",
                        [
                            new WebLink(
                                new string('\u0002', 512),
                                new string('\u0001', 2_048)),
                        ]))));
        ArcanumReadUrlTool tool = new(
            new StubCatalog(provider),
            Settings(),
            NullLogger.Instance);

        string json = Assert.IsType<string>(
            await tool.InvokeAsync(
                new AIFunctionArguments(
                    new Dictionary<string, object?>
                    {
                        ["url"] = "https://example.test/start",
                    }),
                CancellationToken.None));

        Assert.True(
            Encoding.UTF8.GetByteCount(json)
            <= WebToolResultSerializer.MaxUtf8Bytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Contains(
            "Useful page.",
            root.GetProperty("data").GetProperty("markdown").GetString());
        Assert.Empty(
            root.GetProperty("data")
                .GetProperty("links")
                .EnumerateArray());
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    private static StubProvider CreateSearchProvider(
        Func<
            string,
            WebSearchOptions,
            CancellationToken,
            Task<Result<WebSearchResult>>> handler) =>
        new(
            WebResearchProviderNames.Perplexity,
            WebResearchCapabilities.Search,
            handler,
            null);

    private static StubProvider CreateReadProvider(
        Func<
            string,
            WebReadOptions,
            CancellationToken,
            Task<Result<WebReadResult>>> handler) =>
        new(
            WebResearchProviderNames.LocalHttp,
            WebResearchCapabilities.ReadUrl,
            null,
            handler);

    private static TestOptionsSnapshot<ArcanumSettings> Settings(
        string model = WebResearchModels.Sonar) =>
        new(
            new ArcanumSettings
            {
                Features = new FeatureSettings
                {
                    WebBrowsing = true,
                },
                Integrations = new IntegrationSettings
                {
                    WebResearch = new WebResearchIntegrationSettings
                    {
                        PerplexityModel = model,
                    },
                },
            });

    private sealed class StubCatalog(
        params IWebResearchProvider[] providers)
        : IWebResearchProviderCatalog
    {
        public bool TryGetProvider(
            string providerName,
            [NotNullWhen(true)] out IWebResearchProvider? provider)
        {
            provider = providers.FirstOrDefault(
                candidate => string.Equals(
                    candidate.ProviderName,
                    providerName,
                    StringComparison.OrdinalIgnoreCase));

            return provider is not null;
        }
    }

    private sealed class StubProvider(
        string providerName,
        WebResearchCapabilities capabilities,
        Func<
            string,
            WebSearchOptions,
            CancellationToken,
            Task<Result<WebSearchResult>>>? search,
        Func<
            string,
            WebReadOptions,
            CancellationToken,
            Task<Result<WebReadResult>>>? read)
        : IWebResearchProvider
    {
        public string ProviderName => providerName;

        public WebResearchCapabilities Capabilities => capabilities;

        public Task<Result<WebSearchResult>> SearchAsync(
            string query,
            WebSearchOptions options,
            CancellationToken cancellationToken = default) =>
            search is null
                ? Task.FromResult(
                    Result<WebSearchResult>.Failure(
                        new Error(
                            ErrorCodes.WebResearch.UnsupportedOperation,
                            "Unsupported.")))
                : search(query, options, cancellationToken);

        public Task<Result<WebReadResult>> ReadUrlAsync(
            string url,
            WebReadOptions options,
            CancellationToken cancellationToken = default) =>
            read is null
                ? Task.FromResult(
                    Result<WebReadResult>.Failure(
                        new Error(
                            ErrorCodes.WebResearch.UnsupportedOperation,
                            "Unsupported.")))
                : read(url, options, cancellationToken);
    }

}
