using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class CampaignCommandTests
{

    private static readonly Guid SampleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Campaign_list_calls_get_campaigns()
    {

        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(new ListPageResult<CampaignDto>([campaign], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_get_binds_id_argument()
    {

        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignDto>(campaign, true, null),
            ArcanumJsonContext.Default.ApiResponseCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "get", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_get_reports_missing_name_candidate_after_list_lookup()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(
                new ListPageResult<CampaignDto>([], false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "get", "not-a-guid"]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_create_posts_register_request()
    {

        CampaignDto campaign = new(SampleId, "Demo", "/tmp/demo", WorkspaceType.Campaign, null, CampaignSettings.CreateDefault(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignDto>(campaign, true, null),
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            HttpStatusCode.Created));

        CliTestResult result = RunCommand(
            handler,
            ["campaign", "create", "--name", "Demo", "--path", "/tmp/demo"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"name\":\"Demo\"", body, StringComparison.Ordinal);

        Assert.Contains("\"path\":\"/tmp/demo\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Campaign_delete_binds_id_and_handles_no_content()
    {

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["campaign", "delete", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_get_surfaces_not_found_error()
    {

        Error error = new("Campaign.NotFound", "No campaign exists with that identifier.");

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<CampaignDto>(null, false, error),
            ArcanumJsonContext.Default.ApiResponseCampaignDto,
            HttpStatusCode.NotFound));

        CliTestResult result = RunCommand(handler, ["campaign", "get", SampleId.ToString()]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/campaigns/{SampleId:D}", request.RequestUri!.AbsolutePath);

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
