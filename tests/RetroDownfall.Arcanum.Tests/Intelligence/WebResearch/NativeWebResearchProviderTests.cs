using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence.WebResearch;

[Collection("OutboundUrlGuardDns")]
public sealed class NativeWebResearchProviderTests : IDisposable
{
    private readonly IDnsResolver _originalResolver;

    public NativeWebResearchProviderTests()
    {
        _originalResolver = OutboundUrlGuard.DnsResolver;

        FakeDnsResolver dns = new();
        dns.Add("example.test", IPAddress.Parse("93.184.216.34"));
        dns.Add("other.test", IPAddress.Parse("93.184.216.35"));
        dns.Add("private.test", IPAddress.Parse("127.0.0.1"));
        OutboundUrlGuard.DnsResolver = dns;
    }

    public void Dispose() =>
        OutboundUrlGuard.DnsResolver = _originalResolver;

    [Fact]
    public void Catalog_is_case_insensitive_and_rejects_duplicates()
    {
        StubProvider first = new(WebResearchProviderNames.Perplexity);
        WebResearchProviderCatalog catalog = new([first]);

        Assert.True(catalog.TryGetProvider("PERPLEXITY", out IWebResearchProvider? resolved));
        Assert.Same(first, resolved);

        Assert.Throws<InvalidOperationException>(
            () => new WebResearchProviderCatalog(
                [
                    first,
                    new StubProvider("Perplexity"),
                ]));
    }

    [Fact]
    public async Task Perplexity_missing_key_does_not_issue_http()
    {
        RecordingHandler handler = new(
            static (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)));
        PerplexityWebProvider provider = CreatePerplexity(
            handler,
            apiKey: null);

        Result<WebSearchResult> result = await provider.SearchAsync(
            "latest release",
            new WebSearchOptions());

        Assert.True(result.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.MissingCredential,
            result.Error.Code);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Perplexity_posts_fixed_sonar_request_and_maps_citations_and_usage()
    {
        Uri? requestUri = null;
        string? bearer = null;
        string? requestJson = null;
        RecordingHandler handler = new(
            async (request, cancellationToken) =>
            {
                requestUri = request.RequestUri;
                bearer = request.Headers.Authorization?.Parameter;
                requestJson = await request.Content!
                    .ReadAsStringAsync(cancellationToken);

                return JsonResponse(
                    """
                    {
                      "model": "sonar-pro",
                      "choices": [
                        {
                          "message": {
                            "role": "assistant",
                            "content": "Bounded answer [1] [2]"
                          },
                          "finish_reason": "stop"
                        }
                      ],
                      "citations": [
                        "https://example.test/one",
                        "https://other.test/two"
                      ],
                      "search_results": [
                        {
                          "title": "First",
                          "url": "https://example.test/one",
                          "date": "2026-07-29"
                        }
                      ],
                      "usage": {
                        "prompt_tokens": 10,
                        "completion_tokens": 20,
                        "total_tokens": 30,
                        "reasoning_tokens": 4,
                        "citation_tokens": 3,
                        "num_search_queries": 2,
                        "cost": {
                          "total_cost": 0.015
                        }
                      }
                    }
                    """);
            });
        PerplexityWebProvider provider = CreatePerplexity(
            handler,
            "secret-value");

        Result<WebSearchResult> result = await provider.SearchAsync(
            "latest release",
            new WebSearchOptions
            {
                Model = " sonar-pro ",
                MaxCitations = 1,
            });

        Assert.True(result.IsSuccess);
        Assert.Equal(
            WebResearchConstants.PerplexityEndpoint,
            requestUri);
        Assert.Equal("secret-value", bearer);
        Assert.Contains("\"model\":\"sonar-pro\"", requestJson);
        Assert.Contains("\"stream\":false", requestJson);
        Assert.Equal("Bounded answer [1] [2]", result.Value.Answer);
        WebCitation citation = Assert.Single(result.Value.Citations);
        Assert.Equal(1, citation.Index);
        Assert.Equal("First", citation.Title);
        Assert.Equal(1, result.Value.OmittedCitationCount);
        Assert.True(result.Value.Truncated);
        Assert.Equal(30, result.Value.Usage.TotalTokens);
        Assert.Equal(2, result.Value.Usage.SearchQueries);
        Assert.Equal(0.015m, result.Value.Usage.CostUsd);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "WebResearch.AuthenticationOrCreditsFailed")]
    [InlineData(HttpStatusCode.TooManyRequests, "WebResearch.RateLimited")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "WebResearch.RequestRejected")]
    [InlineData(HttpStatusCode.BadGateway, "WebResearch.ProviderUnavailable")]
    public async Task Perplexity_maps_http_status_without_exposing_response_body(
        HttpStatusCode status,
        string expectedCode)
    {
        const string SensitiveBody = "upstream-sensitive-detail";
        RecordingHandler handler = new(
            (_, _) => Task.FromResult(
                JsonResponse(
                    "{\"error\":{\"message\":\""
                    + SensitiveBody
                    + "\"}}",
                    status)));
        PerplexityWebProvider provider = CreatePerplexity(handler, "key");

        Result<WebSearchResult> result = await provider.SearchAsync(
            "query",
            new WebSearchOptions());

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.DoesNotContain(
            SensitiveBody,
            result.Error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Perplexity_uses_stable_error_code_to_distinguish_exhausted_quota()
    {
        RecordingHandler handler = new(
            (_, _) => Task.FromResult(
                JsonResponse(
                    """
                    {
                      "error": {
                        "code": "insufficient_quota",
                        "message": "do not expose me"
                      }
                    }
                    """,
                    HttpStatusCode.Unauthorized)));
        PerplexityWebProvider provider = CreatePerplexity(handler, "key");

        Result<WebSearchResult> result = await provider.SearchAsync(
            "query",
            new WebSearchOptions());

        Assert.True(result.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.QuotaExhausted,
            result.Error.Code);
        Assert.DoesNotContain("do not expose me", result.Error.Message);
    }

    [Fact]
    public async Task Perplexity_distinguishes_internal_timeout_from_caller_cancellation()
    {
        RecordingHandler handler = new(
            static async (_, cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        PerplexityWebProvider provider = CreatePerplexity(handler, "key");

        Result<WebSearchResult> timedOut = await provider.SearchAsync(
            "query",
            new WebSearchOptions
            {
                Timeout = TimeSpan.FromMilliseconds(20),
            });

        Assert.True(timedOut.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.Timeout,
            timedOut.Error.Code);

        using CancellationTokenSource caller = new();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.SearchAsync(
                "query",
                new WebSearchOptions(),
                caller.Token));
    }

    [Fact]
    public async Task Perplexity_strictly_bounds_raw_response_and_utf8_answer()
    {
        RecordingHandler rawHandler = new(
            (_, _) => Task.FromResult(
                JsonResponse(
                    """{"choices":[{"message":{"role":"assistant","content":"answer"}}]}""")));
        Result<WebSearchResult> tooLarge = await CreatePerplexity(
                rawHandler,
                "key")
            .SearchAsync(
                "query",
                new WebSearchOptions { MaxResponseBytes = 16 });

        Assert.True(tooLarge.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.ResponseTooLarge,
            tooLarge.Error.Code);

        RecordingHandler answerHandler = new(
            (_, _) => Task.FromResult(
                JsonResponse(
                    """
                    {
                      "choices": [
                        {
                          "message": {
                            "role": "assistant",
                            "content": "😀😀😀😀😀😀😀😀😀😀"
                          }
                        }
                      ]
                    }
                    """)));
        Result<WebSearchResult> bounded = await CreatePerplexity(
                answerHandler,
                "key")
            .SearchAsync(
                "query",
                new WebSearchOptions { MaxAnswerBytes = 20 });

        Assert.True(bounded.IsSuccess);
        Assert.True(bounded.Value.Truncated);
        Assert.True(
            Encoding.UTF8.GetByteCount(bounded.Value.Answer) <= 20);
        Assert.DoesNotContain('\uFFFD', bounded.Value.Answer);
    }

    [Fact]
    public async Task Local_reader_converts_html_and_bounds_links()
    {
        RecordingHandler handler = new(
            (_, _) => Task.FromResult(
                HtmlResponse(
                    """
                    <!doctype html>
                    <html>
                      <head><title>Example &amp; Test</title></head>
                      <body>
                        <nav>Ignore this</nav>
                        <main>
                          <h1>Heading</h1>
                          <p>Hello <strong>world</strong>.</p>
                          <script>steal()</script>
                          <a href="/one">One</a>
                          <a href="https://other.test/two">Two</a>
                        </main>
                      </body>
                    </html>
                    """)));
        LocalHttpWebProvider provider = CreateLocal(handler);

        Result<WebReadResult> result = await provider.ReadUrlAsync(
            "https://example.test/start",
            new WebReadOptions { MaxLinks = 1 });

        Assert.True(result.IsSuccess);
        Assert.Equal("Example & Test", result.Value.Title);
        Assert.Contains("# Heading", result.Value.Markdown);
        Assert.Contains("**world**", result.Value.Markdown);
        Assert.DoesNotContain("Ignore this", result.Value.Markdown);
        Assert.DoesNotContain("steal", result.Value.Markdown);
        WebLink link = Assert.Single(result.Value.Links);
        Assert.Equal("https://example.test/one", link.Url);
        Assert.Equal(1, result.Value.OmittedLinkCount);
        Assert.True(result.Value.Truncated);
    }

    [Fact]
    public async Task Local_reader_returns_search_guidance_for_forbidden_and_js_shell()
    {
        RecordingHandler forbiddenHandler = new(
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Result<WebReadResult> forbidden = await CreateLocal(forbiddenHandler)
            .ReadUrlAsync(
                "https://example.test/",
                new WebReadOptions());

        Assert.True(forbidden.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.BotProtected,
            forbidden.Error.Code);
        Assert.Contains("web_search", forbidden.Error.Message);

        RecordingHandler shellHandler = new(
            (_, _) => Task.FromResult(
                HtmlResponse(
                    "<html><body><div id=\"__next\"></div><script>render()</script></body></html>")));
        Result<WebReadResult> shell = await CreateLocal(shellHandler)
            .ReadUrlAsync(
                "https://example.test/",
                new WebReadOptions());

        Assert.True(shell.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.JavaScriptRequired,
            shell.Error.Code);
        Assert.Contains("web_search", shell.Error.Message);
    }

    [Fact]
    public async Task Local_reader_revalidates_redirect_targets()
    {
        RecordingHandler handler = new(
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers =
                    {
                        Location = new Uri("http://private.test/secret"),
                    },
                }));
        LocalHttpWebProvider provider = CreateLocal(handler);

        Result<WebReadResult> result = await provider.ReadUrlAsync(
            "https://example.test/",
            new WebReadOptions());

        Assert.True(result.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.SsrfBlocked,
            result.Error.Code);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Local_reader_enforces_response_and_utf8_content_bounds()
    {
        RecordingHandler rawHandler = new(
            (_, _) => Task.FromResult(
                HtmlResponse("<html><body>content</body></html>")));
        Result<WebReadResult> tooLarge = await CreateLocal(rawHandler)
            .ReadUrlAsync(
                "https://example.test/",
                new WebReadOptions { MaxResponseBytes = 8 });

        Assert.True(tooLarge.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.ResponseTooLarge,
            tooLarge.Error.Code);

        RecordingHandler contentHandler = new(
            (_, _) => Task.FromResult(
                HtmlResponse(
                    "<html><body><main><p>😀😀😀😀😀😀😀😀😀😀</p></main></body></html>")));
        Result<WebReadResult> bounded = await CreateLocal(contentHandler)
            .ReadUrlAsync(
                "https://example.test/",
                new WebReadOptions { MaxContentBytes = 20 });

        Assert.True(bounded.IsSuccess);
        Assert.True(bounded.Value.Truncated);
        Assert.True(
            Encoding.UTF8.GetByteCount(bounded.Value.Markdown) <= 20);
        Assert.DoesNotContain('\uFFFD', bounded.Value.Markdown);
    }

    [Fact]
    public async Task Local_reader_rejects_binary_content_and_times_out()
    {
        RecordingHandler binaryHandler = new(
            (_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([0, 1, 2, 3])
                    {
                        Headers =
                        {
                            ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                                "application/octet-stream"),
                        },
                    },
                }));
        Result<WebReadResult> binary = await CreateLocal(binaryHandler)
            .ReadUrlAsync(
                "https://example.test/file",
                new WebReadOptions());

        Assert.True(binary.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.UnsupportedContent,
            binary.Error.Code);

        RecordingHandler timeoutHandler = new(
            static async (_, cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        Result<WebReadResult> timedOut = await CreateLocal(timeoutHandler)
            .ReadUrlAsync(
                "https://example.test/",
                new WebReadOptions
                {
                    Timeout = TimeSpan.FromMilliseconds(20),
                });

        Assert.True(timedOut.IsFailure);
        Assert.Equal(
            ErrorCodes.WebResearch.Timeout,
            timedOut.Error.Code);
    }

    private static PerplexityWebProvider CreatePerplexity(
        HttpMessageHandler handler,
        string? apiKey) =>
        new(
            new FakeHttpClientFactory(handler),
            new StubApiKeyResolver(apiKey),
            NullLogger<PerplexityWebProvider>.Instance);

    private static LocalHttpWebProvider CreateLocal(
        HttpMessageHandler handler) =>
        new(
            new FakeHttpClientFactory(handler),
            new WebPageContentExtractor(),
            NullLogger<LocalHttpWebProvider>.Instance);

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                html,
                Encoding.UTF8,
                "text/html"),
        };

    private sealed class StubApiKeyResolver(string? apiKey)
        : IWebResearchApiKeyResolver
    {
        public ValueTask<string?> ResolveApiKeyAsync(
            string providerName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(apiKey);
    }

    private sealed class StubProvider(string name)
        : IWebResearchProvider
    {
        public string ProviderName => name;

        public WebResearchCapabilities Capabilities =>
            WebResearchCapabilities.None;

        public Task<Result<WebSearchResult>> SearchAsync(
            string query,
            WebSearchOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<WebReadResult>> ReadUrlAsync(
            string url,
            WebReadOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return responder(request, cancellationToken);
        }
    }
}
