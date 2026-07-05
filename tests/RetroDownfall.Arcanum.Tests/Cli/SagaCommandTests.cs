using System.Net;
using System.Text.Json;
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

        Assert.Contains("limit=10", query, StringComparison.Ordinal);

        Assert.Contains("offset=5", query, StringComparison.Ordinal);

    }

    [Fact]
    public void Saga_list_rejects_invalid_session_guid()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["saga", "list", "--session", "not-a-guid"]);

        Assert.Equal(1, result.ExitCode);

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

        Assert.Equal(1, result.ExitCode);

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

        CliTestResult result = RunCommand(handler, ["saga", "delete", "mem-1"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal("/api/saga/mem-1", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Saga_delete_surfaces_not_found()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<string>(null, false, new Error("Saga.NotFound", "Saga memory was not found.")),
            ArcanumJsonContext.Default.ApiResponseString,
            HttpStatusCode.NotFound));

        CliTestResult result = RunCommand(handler, ["saga", "delete", "missing-id"]);

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
