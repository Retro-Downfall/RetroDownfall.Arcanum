using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]

[Collection("GlobalConsole")]

public sealed class MemoryCommandTests
{

    [Fact]

    public void Memory_status_uses_active_session_and_renders_each_distinct_store()
    {

        Guid sessionId = Guid.NewGuid();

        MemoryStatusDto payload = new(
            sessionId,
            "Current task",
            [
                new MemoryStoreStatusDto("Session Entries", true, 4, "session", "Session lifetime"),
                new MemoryStoreStatusDto("Pinned Entries", true, 1, "session", "Until explicitly unpinned or deleted"),
                new MemoryStoreStatusDto("Campaign Summary", true, 1, "session", "Session lifetime"),
                new MemoryStoreStatusDto("Attachments", true, 2, "attachments", "Bound to the session"),
                new MemoryStoreStatusDto("Indexed Attachment Chunks", true, 7, "attachments", "Rebuilt from attachments"),
                new MemoryStoreStatusDto("Lexicon", true, 3, "lexicon", "Durable until explicit entity deletion"),
                new MemoryStoreStatusDto("Saga", true, 5, "saga", "Durable until explicit Saga deletion"),
                new MemoryStoreStatusDto("Workspace Index", true, 11, "workspace", "Rebuilt from workspace files"),
            ]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemoryStatusDto>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemoryStatusDto));

        CliTestResult result = RunCommand(
            handler,
            ["memory", "status", sessionId.ToString("D")]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Session Entries", result.Output, StringComparison.Ordinal);

        Assert.Contains("Lexicon", result.Output, StringComparison.Ordinal);

        Assert.Contains("Workspace Index", result.Output, StringComparison.Ordinal);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/memory/status/{sessionId:D}", request.RequestUri!.AbsolutePath);

    }

    [Fact]

    public void Memory_search_defaults_to_all_and_clearly_displays_scope_provenance_and_retention()
    {

        MemorySearchResponse payload = new(
            "dark mode",
            MemorySearchScope.All,
            [
                new MemorySearchResultDto(
                    MemorySearchScope.Lexicon,
                    "Operator preferences",
                    "Prefers dark mode.",
                    "Lexicon entity: Operator",
                    "Durable until explicit entity deletion",
                    "operator"),
            ]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<MemorySearchResponse>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

        CliTestResult result = RunCommand(handler, ["memory", "search", "dark mode"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Scope: all", result.Output, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Lexicon entity: Operator", result.Output, StringComparison.Ordinal);

        Assert.Contains("Durable until explicit entity deletion", result.Output, StringComparison.Ordinal);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        MemorySearchRequest? sent = JsonSerializer.Deserialize(
            ReadRequestBody(request),
            ArcanumJsonContext.Default.MemorySearchRequest);

        Assert.NotNull(sent);

        Assert.Equal(MemorySearchScope.All, sent.Scope);

    }

    [Fact]

    public void Memory_search_accepts_every_documented_scope_without_extra_enablement_switches()
    {

        foreach (string scope in new[] { "session", "attachments", "workspace", "saga", "lexicon", "all" })
        {

            RecordingHandler handler = new(_ => CreateResponse(
                new ApiResponse<MemorySearchResponse>(
                    new MemorySearchResponse("needle", Enum.Parse<MemorySearchScope>(scope, true), []),
                    true,
                    null),
                ArcanumJsonContext.Default.ApiResponseMemorySearchResponse));

            CliTestResult result = RunCommand(
                handler,
                ["memory", "search", "needle", "--scope", scope]);

            Assert.Equal(0, result.ExitCode);

            Assert.Single(handler.Requests);

        }

    }

    [Fact]

    public void Memory_lexicon_delete_is_explicit_and_calls_item_scoped_endpoint_after_yes()
    {

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(
            handler,
            ["--yes", "memory", "lexicon", "delete", "Operator"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal("/api/memory/lexicon/Operator", request.RequestUri!.AbsolutePath);

    }

    [Fact]

    public void Memory_has_no_generic_delete_command()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["memory", "delete", "anything"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

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

            return Task.FromResult(
                responder is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : responder(request));

        }

    }

}
