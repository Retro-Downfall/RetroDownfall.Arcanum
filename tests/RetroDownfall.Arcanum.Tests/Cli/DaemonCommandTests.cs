using System.Net;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// <c>daemon alert</c> advertises a closed severity set. The parser has to enforce exactly that set:
/// a value the operator cannot see in the help text must never reach the Comm Link dispatcher.
/// </summary>
[Collection("GlobalConsole")]
public sealed class DaemonCommandTests
{

    [Theory]
    [InlineData("9")]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("bogus")]
    public void Alert_rejects_a_severity_outside_the_documented_set_without_calling_the_api(string severity)
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(
            handler,
            ["daemon", "alert", "disk full", "--severity", severity]);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--severity", result.Error, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("Info")]
    [InlineData("warning")]
    [InlineData("CRITICAL")]
    public void Alert_accepts_the_documented_severity_names(string severity)
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<bool>(true, true, null),
            ArcanumJsonContext.Default.ApiResponseBoolean));

        CliTestResult result = RunCommand(
            handler,
            ["daemon", "alert", "disk full", "--severity", severity]);

        Assert.Equal(0, result.ExitCode);

        _ = Assert.Single(handler.Requests);

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
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, typeInfo);

        return new HttpResponseMessage(HttpStatusCode.OK)
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

            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));

            HttpResponseMessage response = responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(request);

            return Task.FromResult(response);

        }

    }

}
