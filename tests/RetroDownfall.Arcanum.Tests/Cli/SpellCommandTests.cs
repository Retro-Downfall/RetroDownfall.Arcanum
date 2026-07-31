using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class SpellCommandTests
{

    [Fact]
    public void Spell_list_calls_get_spells_with_workspace_query()
    {

        SpellSummary summary = new("greet", "Say hello", SpellSource.Workspace, ["demo"]);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary[]>([summary], true, null),
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray));

        CliTestResult result = RunCommand(handler, ["spell", "list", "--workspace", "/tmp/ws"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/spells", request.RequestUri!.AbsolutePath);

        Assert.Contains("workspace=", request.RequestUri!.Query, StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_get_binds_name_argument()
    {

        SpellDetail detail = new(
            "greet",
            "Say hello",
            SpellSource.Workspace,
            [],
            null,
            null,
            "Hello!",
            null,
            null,
            [],
            [],
            "/tmp/ws",
            "/tmp/ws/spells/greet/SPELL.md");

        SpellSummary summary = new("greet", "Say hello", SpellSource.Workspace, []);
        RecordingHandler handler = new(request =>
            request.RequestUri!.AbsolutePath == "/api/spells"
                ? CreateResponse(
                    new ApiResponse<SpellSummary[]>([summary], true, null),
                    ArcanumJsonContext.Default.ApiResponseSpellSummaryArray)
                : CreateResponse(
                    new ApiResponse<SpellDetail>(detail, true, null),
                    ArcanumJsonContext.Default.ApiResponseSpellDetail));

        CliTestResult result = RunCommand(handler, ["spell", "get", "greet"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);
        HttpRequestMessage request = handler.Requests[1];

        Assert.Equal("/api/spells/greet", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Spell_delete_requires_workspace_without_calling_api()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["spell", "delete", "greet"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Spell_execute_posts_prompt_and_prints_response()
    {

        PromptResponseDto response = new("Hello, world!", null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<PromptResponseDto>(response, true, null),
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto));

        CliTestResult result = RunCommand(handler, ["spell", "execute", "greet", "--input", "hi there"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/spells/greet/execute", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"prompt\":\"hi there\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_create_merges_repeated_tag_flags_into_a_single_array()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<bool>(true, true, null),
            ArcanumJsonContext.Default.ApiResponseBoolean));

        CliTestResult result = RunCommand(
            handler,
            ["spell", "create", "--name", "greet", "--workspace", "/tmp/ws", "--tag", "a", "--tag", "b", "--tag", "c"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        string body = ReadBody(request);

        Assert.Contains("\"tags\":[\"a\",\"b\",\"c\"]", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Spell_search_binds_query_options()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary[]>([], true, null),
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray));

        CliTestResult result = RunCommand(handler, ["spell", "search", "--query", "greet", "--tag", "demo"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/spells/search", request.RequestUri!.AbsolutePath);

        Assert.Contains("q=greet", request.RequestUri!.Query, StringComparison.Ordinal);

        Assert.Contains("tag=demo", request.RequestUri!.Query, StringComparison.Ordinal);

    }

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

    private static string ReadBody(HttpRequestMessage request)
    {

        if (request.Content is null)
        {
            return string.Empty;
        }

        return request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

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
