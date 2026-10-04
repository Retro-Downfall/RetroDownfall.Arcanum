using System.Net;

using System.Text;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

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

    /// <summary>
    /// A search whose provider call succeeded but whose <c>--attach-to-session</c> could not be written
    /// returns the paid-for answer with a non-fatal <c>attachmentError</c> instead of failing the whole
    /// workflow. That trade is only honest if the operator is told: an attachment silently not happening
    /// is the same outcome as the workflow lying about having made one, so the error travels to stderr
    /// exactly like the research path's <c>attachment_failed</c> progress frame does, while stdout keeps
    /// carrying only the answer and the exit code stays 0.
    /// </summary>
    [Fact]

    public void Search_reports_a_failed_attachment_on_stderr_without_failing_the_run()
    {
        RecordingHandler handler = new(
            request => JsonResponse(
                """
                {
                  "data": {
                    "answer": "Answer that was already billed.",
                    "citations": [],
                    "provider": "perplexity",
                    "model": "sonar",
                    "truncated": false,
                    "usage": { "totalTokens": 12, "searchQueries": 1 },
                    "attachmentId": null,
                    "attachmentError": "The session was archived before the attachment was written."
                  },
                  "isSuccess": true,
                  "error": null,
                  "traceId": "test"
                }
                """));

        CliTestResult result = RunCommand(
            handler,
            [
                "search",
                "current facts",
                "--attach-to-session",
                "11111111-1111-1111-1111-111111111111",
            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(
            "The session was archived before the attachment was written.",
            result.Error,
            StringComparison.Ordinal);

        Assert.Contains(
            "Answer that was already billed.",
            result.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "The session was archived before the attachment was written.",
            result.Output,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Same contract on the browse path, which shares the fetch-is-already-billed reasoning and the same
    /// non-fatal <c>attachmentError</c> field.
    /// </summary>
    [Fact]

    public void Browse_reports_a_failed_attachment_on_stderr_without_failing_the_run()
    {
        RecordingHandler handler = new(
            request => JsonResponse(
                """
                {
                  "data": {
                    "title": "Page",
                    "markdown": "# Page",
                    "finalUrl": "https://example.test/app",
                    "links": [],
                    "provider": "static",
                    "renderMode": "static",
                    "truncated": false,
                    "attachmentId": null,
                    "attachmentError": "The session was purged before the attachment was written."
                  },
                  "isSuccess": true,
                  "error": null,
                  "traceId": "test"
                }
                """));

        CliTestResult result = RunCommand(
            handler,
            [
                "browse",
                "https://example.test/app",
                "--attach-to-session",
                "11111111-1111-1111-1111-111111111111",
            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(
            "The session was purged before the attachment was written.",
            result.Error,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The successful attachment keeps naming the stored id, so the failure diagnostic is an addition to
    /// the existing contract rather than a replacement for it.
    /// </summary>
    [Fact]

    public void Search_names_the_attachment_id_when_the_attachment_succeeded()
    {
        RecordingHandler handler = new(
            request => JsonResponse(
                """
                {
                  "data": {
                    "answer": "Attached answer.",
                    "citations": [],
                    "provider": "perplexity",
                    "model": "sonar",
                    "truncated": false,
                    "usage": { "totalTokens": 12, "searchQueries": 1 },
                    "attachmentId": "22222222-2222-2222-2222-222222222222",
                    "attachmentError": null
                  },
                  "isSuccess": true,
                  "error": null,
                  "traceId": "test"
                }
                """));

        CliTestResult result = RunCommand(
            handler,
            [
                "search",
                "current facts",
                "--attach-to-session",
                "11111111-1111-1111-1111-111111111111",
            ]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(
            "22222222-2222-2222-2222-222222222222",
            result.Error,
            StringComparison.Ordinal);
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

    /// <summary>
    /// R-067: the <c>--save</c> destination is validated before the (billed) request is sent, so a typo
    /// in the directory costs nothing instead of discarding a finished answer.
    /// </summary>
    [Theory]

    [InlineData("search")]

    [InlineData("browse")]

    [InlineData("research")]

    public void Save_to_a_missing_directory_fails_before_calling_the_host(string command)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-missing-{Guid.NewGuid():N}",
            "answer.md");

        RecordingHandler handler = new();

        string[] arguments = command switch
        {
            "browse" => ["browse", "https://example.test/page", "--save", path],
            _ => [command, "What changed?", "--save", path],
        };

        CliTestResult result = RunCommand(handler, arguments);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--save", result.Error, StringComparison.Ordinal);

        Assert.False(File.Exists(path));
    }

    [Fact]

    public void Research_with_unwritable_save_path_fails_before_calling_the_host()
    {
        // A directory is never a writable file destination on any supported platform.
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-save-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        try
        {
            RecordingHandler handler = new();

            CliTestResult result = RunCommand(
                handler,
                ["research", "What changed?", "--save", path]);

            Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

            Assert.Empty(handler.Requests);

            Assert.Contains("--save", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]

    public void Research_still_prints_the_answer_when_the_late_save_fails()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-late-save-{Guid.NewGuid():N}.md");

        try
        {
            // The destination is valid when the request goes out and unusable by the time the answer
            // comes back: the host call creates a directory where the file was meant to go.
            RecordingHandler handler = new(
                _ =>
                {
                    Directory.CreateDirectory(path);

                    return NdjsonResponse(
                        """
                        {"type":"result","result":{"answer":"Late answer survives.","citations":[],"provider":"perplexity","model":"sonar","truncated":false,"usage":{"totalTokens":4,"searchQueries":1}}}
                        """);
                });

            CliTestResult result = RunCommand(
                handler,
                ["research", "What changed?", "--format", "markdown", "--save", path]);

            Assert.Equal(1, result.ExitCode);

            Assert.Contains("Late answer survives.", result.Output, StringComparison.Ordinal);

            Assert.Contains("Could not save", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path);
            }
        }
    }

    /// <summary>
    /// R-327: <c>--save</c> over an existing file asks first, like every sibling verb that writes an
    /// operator-named file, and a refusal sends nothing to the host.
    /// </summary>
    [Fact]

    public void Save_asks_before_overwriting_an_existing_file_and_sends_nothing_when_declined()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-overwrite-{Guid.NewGuid():N}.md");

        File.WriteAllText(path, "original");

        try
        {
            RecordingPrompt prompt = new(answer: false);

            RecordingHandler handler = new();

            CliTestResult result = RunCommand(
                handler,
                ["search", "saved facts", "--save", path],
                prompt);

            Assert.Equal(0, result.ExitCode);

            Assert.Empty(handler.Requests);

            Assert.Equal("original", File.ReadAllText(path));

            string question = Assert.Single(prompt.Questions);

            Assert.Contains(path, question, StringComparison.Ordinal);

            Assert.Contains("Overwrite", question, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]

    public void Save_replaces_an_existing_file_atomically_once_confirmed()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-overwrite-dir-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, "answer.md");

        File.WriteAllText(path, "original");

        try
        {
            RecordingHandler handler = new(
                _ => JsonResponse(
                    """
                    {
                      "data": {
                        "answer": "Replacement answer.",
                        "citations": [],
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
                ["search", "saved facts", "--save", path],
                new RecordingPrompt(answer: true));

            Assert.Equal(0, result.ExitCode);

            Assert.Contains("Replacement answer.", File.ReadAllText(path), StringComparison.Ordinal);

            Assert.Equal(["answer.md"], Directory.GetFileSystemEntries(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]

    public void Save_over_an_existing_file_refuses_a_non_interactive_run_without_yes()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-overwrite-json-{Guid.NewGuid():N}.md");

        File.WriteAllText(path, "original");

        try
        {
            RecordingHandler handler = new();

            CliTestResult result = RunCommand(
                handler,
                ["--json", "search", "saved facts", "--save", path]);

            Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

            Assert.Empty(handler.Requests);

            Assert.Equal("original", File.ReadAllText(path));

            Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
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

    /// <summary>
    /// A research run that cannot reach the host fails for exactly the reason an <c>ask</c> does, so
    /// it must name the same next step. Printing the transport copy without the hint leaves the
    /// operator with a diagnosis and no command to run.
    /// </summary>
    [Fact]

    public void Research_transport_failure_carries_the_doctor_hint_ask_appends()
    {
        RecordingHandler handler = new(
            _ => throw new HttpRequestException("No listener on the configured port."));

        CliTestResult result = RunCommand(
            handler,
            ["research", "What changed?"]);

        Assert.Equal((int)CliExitCode.NetworkError, result.ExitCode);

        Assert.Contains(
            ArcanumApiClient.StreamUnreachableMessage,
            result.Error,
            StringComparison.Ordinal);

        Assert.Contains(
            ArcanumApiClient.StreamDoctorHint,
            result.Error,
            StringComparison.Ordinal);
    }

    [Fact]

    public void Research_rejects_an_undocumented_format_without_calling_the_api()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["research", "What changed?", "--format", "bogus"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--format", result.Error, StringComparison.Ordinal);
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
        string[] args,
        IConfirmationPrompt? prompt = null)
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

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        if (prompt is not null)
        {
            services.RemoveAll<IConfirmationPrompt>();

            services.AddSingleton(prompt);
        }

        return CliTestHarness.Run(services, args);
    }

    private sealed class RecordingPrompt(bool answer) : IConfirmationPrompt
    {
        public List<string> Questions { get; } = [];

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken)
        {
            Questions.Add(question);

            return Task.FromResult(answer);
        }
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
