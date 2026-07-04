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
using Spectre.Console.Cli.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
public sealed class SpellCloneCommandTests
{

    [Fact]
    public void Spell_clone_posts_new_name_and_prints_confirmation()
    {

        SpellSummary cloned = new("greet-copy", "Say hello", SpellSource.Workspace, []);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary>(cloned, true, null),
            ArcanumJsonContext.Default.ApiResponseSpellSummary,
            HttpStatusCode.Created));

        CommandAppResult result = RunCommand(handler, ["spell", "clone", "greet", "--new-name", "greet-copy", "--workspace", "/tmp/ws"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/spells/greet/clone", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Spell_clone_requires_new_name()
    {

        RecordingHandler handler = new();

        CommandAppResult result = RunCommand(handler, ["spell", "clone", "greet"]);

        Assert.NotEqual(0, result.ExitCode);

    }

    [Fact]
    public void Spell_clone_prints_error_on_name_collision()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary>(null, false, new Error("Spell.NameCollision", "Spell \"greet-copy\" already exists in this workspace.")),
            ArcanumJsonContext.Default.ApiResponseSpellSummary,
            HttpStatusCode.BadRequest));

        CommandAppResult result = RunCommand(handler, ["spell", "clone", "greet", "--new-name", "greet-copy", "--workspace", "/tmp/ws"]);

        Assert.Equal(1, result.ExitCode);

    }

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
