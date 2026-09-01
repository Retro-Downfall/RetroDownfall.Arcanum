using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class SpellVersionCommandTests
{

    [Fact]
    public void Spell_version_create_posts_body_and_prints_confirmation()
    {

        SpellVersionDto version = new("2.0", false, DateTimeOffset.UtcNow, null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellVersionDto>(version, true, null),
            ArcanumJsonContext.Default.ApiResponseSpellVersionDto,
            HttpStatusCode.Created));

        CliTestResult result = RunCommand(
            handler,
            ["spell", "version", "create", "greet", "--version", "2.0", "--body", "New draft body.", "--workspace", "/tmp/ws"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/spells/greet/versions", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Spell_version_update_puts_body_to_version_path()
    {

        SpellVersionDto version = new("2.0", false, DateTimeOffset.UtcNow, null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellVersionDto>(version, true, null),
            ArcanumJsonContext.Default.ApiResponseSpellVersionDto));

        CliTestResult result = RunCommand(
            handler,
            ["spell", "version", "update", "greet", "--version", "2.0", "--body", "Updated body.", "--workspace", "/tmp/ws"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Put, request.Method);

        Assert.Equal("/api/spells/greet/versions/2.0", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Spell_version_activate_posts_to_activate_endpoint_and_prints_previous_version()
    {

        SpellVersionDto version = new("2.0", true, DateTimeOffset.UtcNow, null, PreviousVersion: "1.0");

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellVersionDto>(version, true, null),
            ArcanumJsonContext.Default.ApiResponseSpellVersionDto));

        CliTestResult result = RunCommand(
            handler,
            ["spell", "version", "activate", "greet", "--version", "2.0", "--workspace", "/tmp/ws"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/spells/greet/versions/2.0/activate", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Spell_version_create_requires_body()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["spell", "version", "create", "greet", "--version", "2.0"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

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
