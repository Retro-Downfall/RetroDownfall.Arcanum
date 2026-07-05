using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console.Cli.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// RAG Phase 2 — smoke tests for <c>arcanum session divine</c> (POST /api/sessions/divine).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionDivinationCommandTests
{

    [Fact]
    public void Session_divine_calls_divination_endpoint_and_renders_results()
    {

        Guid sessionId = Guid.NewGuid();

        Guid entryId = Guid.NewGuid();

        SemanticSessionSearchResult hit = new(
            sessionId,
            "Investigating flaky test",
            entryId,
            "assistant",
            "The root cause was a race condition in...",
            0.87f,
            DateTimeOffset.UtcNow);

        SemanticSearchResult payload = new([hit], false, null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SemanticSearchResult>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSemanticSearchResult));

        CommandAppResult result = RunCommand(handler, ["session", "divine", "race condition"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/sessions/divine", request.RequestUri!.AbsolutePath);

        byte[] body = ReadRequestBody(request);

        SemanticSearchRequest? sent = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.SemanticSearchRequest);

        Assert.NotNull(sent);

        Assert.Equal("race condition", sent.Query);

    }

    [Fact]
    public void Session_divine_passes_limit_campaign_and_status_options()
    {

        Guid campaignId = Guid.NewGuid();

        SemanticSearchResult payload = new([], false, null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SemanticSearchResult>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSemanticSearchResult));

        CommandAppResult result = RunCommand(
            handler,
            ["session", "divine", "hello", "--limit", "3", "--campaign", campaignId.ToString(), "--status", "archived"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        byte[] body = ReadRequestBody(request);

        SemanticSearchRequest? sent = JsonSerializer.Deserialize(body, ArcanumJsonContext.Default.SemanticSearchRequest);

        Assert.NotNull(sent);

        Assert.Equal(3, sent.Limit);

        Assert.Equal(campaignId, sent.CampaignId);

        Assert.Equal("archived", sent.Status);

    }

    [Fact]
    public void Session_divine_rejects_invalid_campaign_guid()
    {

        RecordingHandler handler = new();

        CommandAppResult result = RunCommand(handler, ["session", "divine", "hello", "--campaign", "not-a-guid"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Session_divine_surfaces_api_failure()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SemanticSearchResult>(
                null,
                false,
                new Error("Embeddings.FeatureDisabled", "Session semantic search is disabled.")),
            ArcanumJsonContext.Default.ApiResponseSemanticSearchResult,
            HttpStatusCode.ServiceUnavailable));

        CommandAppResult result = RunCommand(handler, ["session", "divine", "hello"]);

        Assert.Equal(1, result.ExitCode);

    }

    private static byte[] ReadRequestBody(HttpRequestMessage request) =>
        request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();

    private static CommandAppResult RunCommand(RecordingHandler handler, string[] args)
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore("test-key"));

        CommandAppTester tester = new(new CliTypeRegistrar(services));

        tester.Configure(CliApplicationFactory.ConfigureCommands);

        return tester.Run(args);

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
