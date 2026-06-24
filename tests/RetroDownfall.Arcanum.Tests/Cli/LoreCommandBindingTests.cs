using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console.Cli.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
public sealed class LoreCommandBindingTests
{

    [Fact]
    public void Lore_set_binds_key_and_value_arguments()
    {

        RecordingHandler handler = CreateLoreHandler();

        CommandAppResult result = RunCommand(handler, ["lore", "set", "ward.color", "cobalt"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/lore", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"key\":\"ward.color\"", body, StringComparison.Ordinal);

        Assert.Contains("\"value\":\"cobalt\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Lore_get_binds_key_argument()
    {

        RecordingHandler handler = CreateLoreHandler();

        CommandAppResult result = RunCommand(handler, ["lore", "get", "ward.color"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/lore/ward.color", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Lore_delete_binds_key_argument()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        CommandAppResult result = RunCommand(handler, ["lore", "delete", "ward.color"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal("/api/lore/ward.color", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Daemon_initiative_binds_job_name_and_minutes()
    {

        RecordingHandler handler = new(_ => CreateDaemonResponse(
            new ApiResponse<UnseenServantJobStatusDto>(
                new UnseenServantJobStatusDto("heartbeat", "spell", 5, 15, true),
                true,
                null)));

        CommandAppResult result = RunCommand(handler, ["daemon", "initiative", "heartbeat", "15"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/unseen-servant/jobs/heartbeat/initiative", request.RequestUri!.AbsolutePath);

        Assert.Contains("\"intervalMinutes\":15", ReadBody(request), StringComparison.Ordinal);

    }

    [Fact]
    public void Daemon_alert_binds_message_and_options()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        CommandAppResult result = RunCommand(
            handler,
            ["daemon", "alert", "Disk full", "--title", "Ops", "--severity", "Warning", "--source", "test"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/commlink/send", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("Disk full", body, StringComparison.Ordinal);

        Assert.Contains("\"title\":\"Ops\"", body, StringComparison.Ordinal);

        Assert.Contains("\"source\":\"test\"", body, StringComparison.Ordinal);

        Assert.Contains("\"body\":\"Disk full\"", body, StringComparison.Ordinal);

    }

    private static RecordingHandler CreateLoreHandler() =>
        new(_ => CreateLoreResponse(new ApiResponse<LoreDto>(
            new LoreDto("ward.color", "cobalt", DateTime.UtcNow),
            true,
            null)));

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

    private static HttpResponseMessage CreateLoreResponse(ApiResponse<LoreDto> envelope)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseLoreDto);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private static HttpResponseMessage CreateDaemonResponse(ApiResponse<UnseenServantJobStatusDto> envelope)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };

    }

    private static HttpResponseMessage CreateBooleanResponse(ApiResponse<bool> envelope)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseBoolean);

        return new HttpResponseMessage(HttpStatusCode.OK)
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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {

                snapshot.Headers.TryAddWithoutValidation(header.Key, header.Value);

            }

            Requests.Add(snapshot);

            return Task.FromResult(responder(request));

        }

    }

}
