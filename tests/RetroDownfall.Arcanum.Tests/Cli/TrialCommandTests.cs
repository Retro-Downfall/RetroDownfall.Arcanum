using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class TrialCommandTests
{

    [Fact]
    public void Trial_run_posts_trial_body_with_target_and_variables()
    {

        TrialResult trialResult = new(
            "Trial",
            TrialTargetKind.Spell,
            "greet",
            true,
            "Hello, Ada!",
            [new InquisitorVerdict("regex", null, true, "matched")],
            1,
            1,
            null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<TrialResult>(trialResult, true, null),
            ArcanumJsonContext.Default.ApiResponseTrialResult));

        CliTestResult result = RunCommand(
            handler,
            [
                "trial", "run",
                "--target", "spell",
                "--target-value", "greet",
                "--var", "name=Ada",
                "--inquisitor", "{\"kind\":\"regex\",\"pattern\":\"Hello\"}",
            ]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/proving-grounds/trials/run", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"target\":\"greet\"", body, StringComparison.Ordinal);

        Assert.Contains("\"name\":\"Ada\"", body, StringComparison.Ordinal);

        Assert.Contains("\"kind\":\"regex\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Trial_run_rejects_invalid_target_without_calling_api()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["trial", "run", "--target", "bogus", "--target-value", "x"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Trial_run_exits_nonzero_when_trial_fails()
    {

        TrialResult trialResult = new(
            "Trial",
            TrialTargetKind.Spell,
            "greet",
            false,
            "Goodbye!",
            [new InquisitorVerdict("regex", null, false, "no match")],
            0,
            1,
            null);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<TrialResult>(trialResult, true, null),
            ArcanumJsonContext.Default.ApiResponseTrialResult));

        CliTestResult result = RunCommand(
            handler,
            ["trial", "run", "--target", "spell", "--target-value", "greet"]);

        Assert.Equal(1, result.ExitCode);

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
