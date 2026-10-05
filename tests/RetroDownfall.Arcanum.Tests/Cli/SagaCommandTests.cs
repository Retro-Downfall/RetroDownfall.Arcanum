using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// RAG Phase 4 — smoke tests for <c>arcanum saga list|divine|delete|stats</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class SagaCommandTests
{
    [Fact]
    public void Saga_list_calls_list_endpoint_and_renders_results()
    {
        SagaMemoryDto[] payload =
        [
            new SagaMemoryDto("mem-1", "The operator prefers dark mode.", DateTimeOffset.UtcNow, null, null, "extraction"),
        ];

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaMemoryDto[]>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray));

        CliTestResult result = RunCommand(handler, ["saga", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/saga", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// The listing reads memory rows, so a retired memory is listed beside live ones; the State column
    /// is what keeps an operator from reading it as something a turn can recall.
    /// </summary>
    [Fact]
    public void Saga_list_marks_retired_and_pinned_memories()
    {
        DateTimeOffset retiredAt = new(2026, 9, 20, 12, 34, 56, TimeSpan.Zero);

        DateTimeOffset pinnedAt = new(2026, 9, 21, 1, 2, 3, TimeSpan.Zero);

        SagaMemoryDto[] payload =
        [
            new("mem-actv", "a", DateTimeOffset.UnixEpoch, null, null, null),
            new("mem-retd", "b", DateTimeOffset.UnixEpoch, null, null, null, RetiredAtUtc: retiredAt),
            new("mem-pind", "c", DateTimeOffset.UnixEpoch, null, null, null, PinnedAtUtc: pinnedAt),
            new("mem-both", "d", DateTimeOffset.UnixEpoch, null, null, null, RetiredAtUtc: retiredAt, PinnedAtUtc: pinnedAt),
        ];

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaMemoryDto[]>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray));

        CliTestResult result = RunCommand(handler, ["saga", "list"]);

        Assert.Equal(0, result.ExitCode);

        string[] lines = result.Output.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains(lines, static line => line.Contains("State", StringComparison.Ordinal));

        Assert.Equal("active", StateCell(lines, "mem-actv"));

        Assert.Equal("retired", StateCell(lines, "mem-retd"));

        Assert.Equal("pinned", StateCell(lines, "mem-pind"));

        Assert.Equal("retired, pinned", StateCell(lines, "mem-both"));
    }

    /// <summary>The last cell of the one table row naming <paramref name="id"/>.</summary>
    private static string StateCell(string[] lines, string id) =>
        Assert.Single(lines, line => line.Contains(id, StringComparison.Ordinal))
            .Split(['│', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1];

    [Fact]
    public void Saga_list_passes_query_session_limit_and_offset_options()
    {
        Guid sessionId = Guid.NewGuid();

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaMemoryDto[]>([], true, null),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray));

        CliTestResult result = RunCommand(
            handler,
            ["saga", "list", "--query", "dark mode", "--session", sessionId.ToString(), "--limit", "10", "--offset", "5"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        string query = request.RequestUri!.Query;

        Assert.Contains("q=dark", query, StringComparison.Ordinal);

        Assert.Contains($"sessionId={sessionId:D}", query, StringComparison.Ordinal);

        // One row beyond the requested ten, so the listing can tell a full page from a prefix.
        Assert.Contains("limit=11", query, StringComparison.Ordinal);

        Assert.Contains("offset=5", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host returns a bare array with no page marker, so the listing asks for one row beyond what it
    /// shows: an extra row is the host's proof that the page was a prefix, and the notice names the
    /// offset that continues it.
    /// </summary>
    [Fact]
    public void List_reports_when_a_prefix_was_shown()
    {
        RecordingHandler handler = new(request =>
        {
            int requested = int.Parse(
                System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["limit"]!,
                System.Globalization.CultureInfo.InvariantCulture);

            SagaMemoryDto[] rows = Enumerable
                .Range(1, Math.Min(requested, 5))
                .Select(static index => new SagaMemoryDto($"mem-{index:D4}", $"payload{index}", DateTimeOffset.UnixEpoch, null, null, null))
                .ToArray();

            return CreateResponse(
                new ApiResponse<SagaMemoryDto[]>(rows, true, null),
                ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);
        });

        CliTestResult result = RunCommand(handler, ["saga", "list", "--limit", "3"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Contains("limit=4", request.RequestUri!.Query, StringComparison.Ordinal);

        Assert.Contains("payload3", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("payload4", result.Output, StringComparison.Ordinal);

        Assert.Contains("--offset 3", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void List_says_nothing_when_the_page_was_the_whole_listing()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaMemoryDto[]>(
                [new SagaMemoryDto("mem-0001", "payload1", DateTimeOffset.UnixEpoch, null, null, null)],
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray));

        CliTestResult result = RunCommand(handler, ["saga", "list", "--limit", "3"]);

        Assert.Equal(0, result.ExitCode);

        Assert.DoesNotContain("--offset", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without <c>--limit</c> the listing is every memory, as it is for every other list: the host bounds
    /// each request and returns a bare array, so the listing reads pages until one is short.
    /// </summary>
    [Fact]
    public void List_without_a_limit_follows_the_host_to_the_last_memory()
    {
        List<string> queries = [];

        RecordingHandler handler = new(request =>
        {
            System.Collections.Specialized.NameValueCollection query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);

            queries.Add(request.RequestUri.Query);

            int limit = int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture);

            int offset = int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture);

            SagaMemoryDto[] rows = Enumerable
                .Range(offset + 1, Math.Max(0, Math.Min(limit, 250 - offset)))
                .Select(static index => new SagaMemoryDto($"mem-{index:D4}", $"payload{index}", DateTimeOffset.UnixEpoch, null, null, null))
                .ToArray();

            return CreateResponse(
                new ApiResponse<SagaMemoryDto[]>(rows, true, null),
                ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);
        });

        CliTestResult result = RunCommand(handler, ["saga", "list"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(3, queries.Count);

        Assert.Contains("offset=100", queries[1], StringComparison.Ordinal);

        Assert.Contains("offset=200", queries[2], StringComparison.Ordinal);

        Assert.Contains("payload1 ", result.Output + " ", StringComparison.Ordinal);

        Assert.Contains("payload250", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("--offset", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that returns the same rows whatever the offset would be followed for ever; it is refused
    /// with the shared no-progress fault, and no partial listing is printed.
    /// </summary>
    [Fact]
    public void List_without_a_limit_refuses_a_host_that_ignores_the_offset()
    {
        int requests = 0;

        RecordingHandler handler = new(_ =>
        {
            requests++;

            if (requests > 12)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            SagaMemoryDto[] rows = Enumerable
                .Range(1, 100)
                .Select(static index => new SagaMemoryDto($"mem-{index:D4}", $"payload{index}", DateTimeOffset.UnixEpoch, null, null, null))
                .ToArray();

            return CreateResponse(
                new ApiResponse<SagaMemoryDto[]>(rows, true, null),
                ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);
        });

        CliTestResult result = RunCommand(handler, ["saga", "list"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Equal(2, requests);

        Assert.Contains("Api.PaginationNoProgress", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain("payload1", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--offset</c> alone starts the listing at that row and still follows it to its end.
    /// </summary>
    [Fact]
    public void List_without_a_limit_starts_at_the_offset_it_was_given()
    {
        List<string> queries = [];

        RecordingHandler handler = new(request =>
        {
            queries.Add(request.RequestUri!.Query);

            return CreateResponse(
                new ApiResponse<SagaMemoryDto[]>(
                    [new SagaMemoryDto("mem-0051", "payload51", DateTimeOffset.UnixEpoch, null, null, null)],
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray);
        });

        CliTestResult result = RunCommand(handler, ["saga", "list", "--offset", "50"]);

        Assert.Equal(0, result.ExitCode);

        string only = Assert.Single(queries);

        Assert.Contains("offset=50", only, StringComparison.Ordinal);

        Assert.Contains("limit=100", only, StringComparison.Ordinal);
    }

    /// <summary>
    /// The listing is where an operator reads the identifier to hand to <c>saga delete</c>, so it
    /// prints the whole identifier rather than a fragment no verb accepts.
    /// </summary>
    [Fact]
    public void List_prints_the_full_memory_identifier()
    {
        const string FullId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaMemoryDto[]>(
                [new SagaMemoryDto(FullId, "payload1", DateTimeOffset.UnixEpoch, null, null, null)],
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDtoArray));

        CliTestResult result = RunCommand(handler, ["saga", "list"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains(FullId, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Saga_list_rejects_invalid_session_guid()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["saga", "list", "--session", "not-a-guid"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Saga_divine_calls_divination_endpoint_and_renders_results()
    {
        SagaMemoryDto memory = new("mem-1", "The operator prefers dark mode.", DateTimeOffset.UtcNow, null, null, "extraction");

        SagaSearchResult payload = new([memory], [0.87f]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaSearchResult>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSagaSearchResult));

        CliTestResult result = RunCommand(handler, ["saga", "divine", "what theme do I like?"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/saga/divine", request.RequestUri!.AbsolutePath);

        byte[] body = ReadRequestBody(request);

        SagaSearchRequest? sent = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.SagaSearchRequest);

        Assert.NotNull(sent);

        Assert.Equal("what theme do I like?", sent.Query);
    }

    [Fact]
    public void Saga_divine_passes_limit_option()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaSearchResult>(new SagaSearchResult([], []), true, null),
            ArcanumJsonContext.Default.ApiResponseSagaSearchResult));

        CliTestResult result = RunCommand(handler, ["saga", "divine", "hello", "--limit", "3"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        byte[] body = ReadRequestBody(request);

        SagaSearchRequest? sent = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.SagaSearchRequest);

        Assert.NotNull(sent);

        Assert.Equal(3, sent.Limit);
    }

    [Fact]
    public void Saga_divine_rejects_empty_query()
    {
        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["saga", "divine", "   "]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Saga_divine_surfaces_api_failure()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaSearchResult>(
                null,
                false,
                new Error("Embeddings.FeatureDisabled", "Saga is disabled.")),
            ArcanumJsonContext.Default.ApiResponseSagaSearchResult,
            HttpStatusCode.ServiceUnavailable));

        CliTestResult result = RunCommand(handler, ["saga", "divine", "hello"]);

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Saga_delete_calls_delete_endpoint()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["--yes", "saga", "delete", "mem-1"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal("/api/saga/mem-1", request.RequestUri!.AbsolutePath);

        // Spectre wraps long lines on the console the harness captures, so the sentence is read with
        // its whitespace collapsed rather than at the column the wrap happened to choose.
        string output = Regex.Replace(result.Output, @"\s+", " ");

        Assert.Contains("was deleted. No suppression fingerprint was recorded", output, StringComparison.Ordinal);

        Assert.Contains("arcanum memory saga erase", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An irreversible delete must ask before it acts. Without <c>--yes</c> and with stdout
    /// redirected (as it always is under this harness), <see cref="ConfirmationPrompt"/> fails closed
    /// rather than silently deleting the named memory.
    /// </summary>
    [Fact]
    public void Saga_delete_requires_confirmation_before_sending_request()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["saga", "delete", "mem-1"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Saga_delete_surfaces_not_found()
    {
        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<string>(null, false, new Error("Saga.NotFound", "Saga memory was not found.")),
            ArcanumJsonContext.Default.ApiResponseString,
            HttpStatusCode.NotFound));

        CliTestResult result = RunCommand(handler, ["--yes", "saga", "delete", "missing-id"]);

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void Saga_stats_calls_stats_endpoint_and_renders_panel()
    {
        SagaStats payload = new(42, 7, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SagaStats>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSagaStats));

        CliTestResult result = RunCommand(handler, ["saga", "stats"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/saga/stats", request.RequestUri!.AbsolutePath);
    }

    private static byte[] ReadRequestBody(HttpRequestMessage request) =>
        request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();

    private static CliTestResult RunCommand(RecordingHandler handler, string[] args)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        CliTestHarness.AddKeyedArcanumResponder(
            services,
            "test-key");

        return CliTestHarness.Run(services, args);
    }

    private static HttpResponseMessage CreateResponse<T>(
        ApiResponse<T> envelope,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(json),
        };
    }

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpRequestMessage snapshot = new(request.Method, request.RequestUri);

            if (request.Content is not null)
            {
                byte[] body = request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

                snapshot.Content = new ByteArrayContent(body);

                foreach (KeyValuePair<string, IEnumerable<string>> contentHeader in request.Content.Headers)
                {
                    snapshot.Content.Headers.TryAddWithoutValidation(contentHeader.Key, contentHeader.Value);
                }
            }

            Requests.Add(snapshot);

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);
        }
    }
}
