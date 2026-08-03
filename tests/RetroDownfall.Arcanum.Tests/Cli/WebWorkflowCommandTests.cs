using System.Net;

using System.Text;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class WebWorkflowCommandTests
{

    [Theory]

    [InlineData("search", "--count", "--freshness", "--include-domain", "--exclude-domain")]

    [InlineData("browse", "--render", "--save", "--attach-to-session")]

    [InlineData("research", "--sources", "--token-budget", "--continue-session")]

    public void Help_exposes_first_class_web_workflows(
        string command,
        params string[] expected)
    {

        CliTestResult result = RunCommand(
            new RecordingHandler(),
            [command, "--help"]);

        Assert.Equal(0, result.ExitCode);

        foreach (string option in expected)
        {

            Assert.Contains(option, result.Output, StringComparison.Ordinal);

        }

    }

    [Fact]

    public void Search_posts_filters_and_writes_one_typed_json_payload()
    {

        RecordingHandler handler = new(
            request => JsonResponse(
                """
                {
                  "data": {
                    "answer": "Current answer.",
                    "citations": [
                      { "index": 1, "url": "https://example.test/source", "title": "Source" }
                    ],
                    "provider": "perplexity",
                    "model": "sonar",
                    "truncated": false,
                    "usage": { "totalTokens": 12, "searchQueries": 1 }
                  },
                  "isSuccess": true,
                  "error": null,
                  "traceId": "test"
                }
                """));

        CliTestResult result = RunCommand(
            handler,
            [
                "--json",
                "search",
                "current facts",
                "--count",
                "3",
                "--freshness",
                "week",
                "--include-domain",
                "example.test",
                "--exclude-domain",
                "ads.example.test",
            ]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/web/search", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"query\":\"current facts\"", body, StringComparison.Ordinal);

        Assert.Contains("\"resultCount\":3", body, StringComparison.Ordinal);

        Assert.Contains("\"freshness\":\"week\"", body, StringComparison.Ordinal);

        Assert.Contains("\"includeDomains\":[\"example.test\"]", body, StringComparison.Ordinal);

        Assert.Contains("\"excludeDomains\":[\"ads.example.test\"]", body, StringComparison.Ordinal);

        Assert.StartsWith("{", result.Output.Trim(), StringComparison.Ordinal);

        Assert.Contains("\"citations\"", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Searching", result.Output, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Browse_posts_render_mode_and_reports_javascript_degradation()
    {

        RecordingHandler handler = new(
            request => JsonResponse(
                """
                {
                  "data": null,
                  "isSuccess": false,
                  "error": {
                    "code": "WebResearch.JavaScriptRenderingUnavailable",
                    "message": "JavaScript rendering is not configured; retry with --render static."
                  },
                  "traceId": "test"
                }
                """,
                HttpStatusCode.ServiceUnavailable));

        CliTestResult result = RunCommand(
            handler,
            ["browse", "https://example.test/app", "--render", "javascript"]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/web/browse", request.RequestUri!.AbsolutePath);

        Assert.Contains(
            "\"renderMode\":\"javascript\"",
            ReadBody(request),
            StringComparison.Ordinal);

        Assert.Contains("--render static", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Search_save_writes_final_markdown_with_citations()
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-search-{Guid.NewGuid():N}.md");

        try
        {

            RecordingHandler handler = new(
                request => JsonResponse(
                    """
                    {
                      "data": {
                        "answer": "Saved answer [1].",
                        "citations": [
                          { "index": 1, "url": "https://example.test/source", "title": "Source" }
                        ],
                        "provider": "perplexity",
                        "model": "sonar",
                        "truncated": false,
                        "usage": { "totalTokens": 12, "searchQueries": 1 }
                      },
                      "isSuccess": true,
                      "error": null,
                      "traceId": "test"
                    }
                    """));

            CliTestResult result = RunCommand(
                handler,
                ["search", "saved facts", "--save", path]);

            Assert.Equal(0, result.ExitCode);

            string saved = File.ReadAllText(path);

            Assert.Contains("Saved answer [1].", saved, StringComparison.Ordinal);

            Assert.Contains(
                "[1]: https://example.test/source",
                saved,
                StringComparison.Ordinal);

            Assert.Contains("Saved", result.Error, StringComparison.Ordinal);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    [Fact]

    public void Research_stream_keeps_progress_on_stderr_and_markdown_on_stdout()
    {

        RecordingHandler handler = new(
            request => NdjsonResponse(
                """
                {"type":"limits","message":"Policy: continue while new sources are discovered; an explicit target of 4 unique sources, 1200 synthesis tokens, $0.25."}
                {"type":"progress","stage":"searching","message":"Searching research pass 1."}
                {"type":"progress","stage":"fetching","message":"Fetching source 1 of 1."}
                {"type":"progress","stage":"rendering","message":"Rendering source 1 of 1."}
                {"type":"progress","stage":"synthesizing","message":"Synthesizing the final answer."}
                {"type":"result","result":{"answer":"## Finding\n\nSupported claim [1].","citations":[{"index":1,"url":"https://example.test/source","title":"Source"}],"provider":"perplexity","model":"sonar","sessionId":"11111111-1111-1111-1111-111111111111","truncated":false,"usage":{"totalTokens":42,"searchQueries":2}}}
                """));

        CliTestResult result = RunCommand(
            handler,
            [
                "research",
                "What changed?",
                "--sources",
                "4",
                "--token-budget",
                "1200",
                "--cost-budget",
                "0.25",
                "--model",
                "sonar",
                "--continue-session",
                "11111111-1111-1111-1111-111111111111",
                "--format",
                "markdown",
            ]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/web/research", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"sourceTarget\":4", body, StringComparison.Ordinal);

        Assert.Contains("\"tokenBudget\":1200", body, StringComparison.Ordinal);

        Assert.Contains("\"costBudgetUsd\":0.25", body, StringComparison.Ordinal);

        Assert.Contains("## Finding", result.Output, StringComparison.Ordinal);

        Assert.Contains("[1]: https://example.test/source", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Searching", result.Output, StringComparison.Ordinal);

        Assert.Contains("Policy:", result.Error, StringComparison.Ordinal);

        Assert.Contains("Searching", result.Error, StringComparison.Ordinal);

        Assert.Contains("Fetching", result.Error, StringComparison.Ordinal);

        Assert.Contains("Rendering", result.Error, StringComparison.Ordinal);

        Assert.Contains("Synthesizing", result.Error, StringComparison.Ordinal);

    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {

            Content = new StringContent(json, Encoding.UTF8, "application/json"),

        };

    private static HttpResponseMessage NdjsonResponse(string ndjson) =>
        new(HttpStatusCode.OK)
        {

            Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),

        };

    private static string ReadBody(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
        ?? string.Empty;

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(
            new FakeSecretStore("test-key"));

        return CliTestHarness.Run(services, args);

    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(
        RecordingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

            };

    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {

                byte[] body = request.Content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                snapshot.Content = new ByteArrayContent(body);

            }

            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);

        }

    }

}
