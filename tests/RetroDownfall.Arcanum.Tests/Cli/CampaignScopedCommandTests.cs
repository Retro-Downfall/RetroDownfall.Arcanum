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
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class CampaignScopedCommandTests
{

    private static readonly Guid SampleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Campaign_spells_calls_scoped_endpoint()
    {

        SpellSummary summary = new("campaign-spell", "A campaign spell", SpellSource.Campaign, []);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SpellSummary[]>([summary], true, null),
            ArcanumJsonContext.Default.ApiResponseSpellSummaryArray));

        CliTestResult result = RunCommand(handler, ["campaign", "spells", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}/spells", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_prompts_calls_scoped_endpoint()
    {

        PromptSummaryDto summary = new(Guid.NewGuid(), SampleId, "campaign-prompt", "1.0.0", null, [], DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<PromptSummaryDto>>(new ListPageResult<PromptSummaryDto>([summary], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto));

        CliTestResult result = RunCommand(handler, ["campaign", "prompts", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}/prompts", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_sessions_calls_scoped_endpoint_and_prints_pagination_note()
    {

        SessionSummaryDto summary = new(Guid.NewGuid(), SampleId, "Scoped session", "active", 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        SessionQueryResult queryResult = new([summary], summary.UpdatedAt, true);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<SessionQueryResult>(queryResult, true, null),
            ArcanumJsonContext.Default.ApiResponseSessionQueryResult));

        CliTestResult result = RunCommand(handler, ["campaign", "sessions", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal($"/api/campaigns/{SampleId:D}/sessions", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Campaign_spells_reports_missing_name_candidate_after_list_lookup()
    {

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<CampaignDto>>(
                new ListPageResult<CampaignDto>([], false),
                true,
                null),
            ArcanumJsonContext.Default.ApiResponseListPageResultCampaignDto));

        CliTestResult result = RunCommand(handler, ["campaign", "spells", "not-a-guid"]);

        Assert.Equal(1, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);
        Assert.Equal("/api/campaigns", request.RequestUri!.AbsolutePath);

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
