using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Wards;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class WardCommandTests
{

    [Fact]
    public void Ward_list_calls_get_wards()
    {

        WardDto ward = new("ward-1", "execute_command", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WardDto[]>([ward], true, null),
            ArcanumJsonContext.Default.ApiResponseWardDtoArray));

        CliTestResult result = RunCommand(handler, ["ward", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/wards", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Ward_get_binds_id_argument()
    {

        WardDto ward = new("ward-1", "execute_command", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WardDto>(ward, true, null),
            ArcanumJsonContext.Default.ApiResponseWardDto));

        CliTestResult result = RunCommand(handler, ["ward", "show", "ward-1"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/wards/ward-1", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Ward_resolve_allow_posts_allow_true()
    {

        WardResolutionDto resolution = new("ward-1", true, "looks safe", DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WardResolutionDto>(resolution, true, null),
            ArcanumJsonContext.Default.ApiResponseWardResolutionDto));

        CliTestResult result = RunCommand(handler, ["ward", "resolve", "ward-1", "--allow", "--reason", "looks safe"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/wards/ward-1", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"allow\":true", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Ward_resolve_deny_posts_allow_false()
    {

        WardResolutionDto resolution = new("ward-1", false, null, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<WardResolutionDto>(resolution, true, null),
            ArcanumJsonContext.Default.ApiResponseWardResolutionDto));

        CliTestResult result = RunCommand(handler, ["ward", "resolve", "ward-1", "--deny"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        string body = ReadBody(request);

        Assert.Contains("\"allow\":false", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Ward_resolve_requires_exactly_one_of_allow_or_deny()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["ward", "resolve", "ward-1"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Ward_resolve_rejects_both_allow_and_deny()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["ward", "resolve", "ward-1", "--allow", "--deny"]);

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
