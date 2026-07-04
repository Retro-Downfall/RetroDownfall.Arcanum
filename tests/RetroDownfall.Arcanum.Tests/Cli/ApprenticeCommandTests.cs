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

[Trait("Category", "Integration")]
public sealed class ApprenticeCommandTests
{

    private static readonly Guid SampleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Apprentice_list_calls_get_apprentices()
    {

        ApprenticeSummaryDto summary = new(SampleId, null, "Task", "Do the thing", "Idle", 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<ApprenticeSummaryDto>>(new ListPageResult<ApprenticeSummaryDto>([summary], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultApprenticeSummaryDto));

        CommandAppResult result = RunCommand(handler, ["apprentice", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/apprentices", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Apprentice_create_posts_goal_and_derives_name()
    {

        ApprenticeDetailDto detail = new(
            SampleId, null, null, "Do the thing", "Do the thing", [], 0, "Idle", null, "/tmp/ws", null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ApprenticeDetailDto>(detail, true, null),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            HttpStatusCode.Created));

        CommandAppResult result = RunCommand(handler, ["apprentice", "create", "--goal", "Do the thing"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/apprentices", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"goal\":\"Do the thing\"", body, StringComparison.Ordinal);

        Assert.Contains("\"name\":\"Do the thing\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Apprentice_start_posts_to_start_route()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<string>(SampleId.ToString("D"), true, null),
            ArcanumJsonContext.Default.ApiResponseString,
            HttpStatusCode.Accepted));

        CommandAppResult result = RunCommand(handler, ["apprentice", "start", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal($"/api/apprentices/{SampleId:D}/start", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Apprentice_cast_surfaces_conclave_disabled_error()
    {

        Error error = new("Apprentice.ConclaveDisabled", "The Conclave is disabled.");

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ApprenticeDetailDto>(null, false, error),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto,
            HttpStatusCode.Conflict));

        CommandAppResult result = RunCommand(handler, ["apprentice", "cast", SampleId.ToString(), "--goal", "Sub-goal"]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/apprentices/{SampleId:D}/cast", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Apprentice_delete_binds_id_and_handles_no_content()
    {

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CommandAppResult result = RunCommand(handler, ["apprentice", "delete", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

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
