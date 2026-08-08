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

namespace RetroDownfall.Arcanum.Tests.Cli;

[Trait("Category", "Integration")]
[Collection("GlobalConsole")]
public sealed class PromptCommandTests
{

    private static readonly Guid SampleId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Prompt_list_calls_get_prompts()
    {

        PromptSummaryDto summary = new(SampleId, null, "greeting", "1", null, [], DateTimeOffset.UtcNow);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<ListPageResult<PromptSummaryDto>>(new ListPageResult<PromptSummaryDto>([summary], false), true, null),
            ArcanumJsonContext.Default.ApiResponseListPageResultPromptSummaryDto));

        CliTestResult result = RunCommand(handler, ["prompt", "list"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal("/api/prompts", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Prompt_get_binds_id_argument()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        PromptDetailDto detail = new(
            SampleId,
            CampaignId: null,
            Name: "greeting",
            Version: "1",
            Description: null,
            Tags: [],
            Template: "Hello {{name}}",
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            CreatedAt: now,
            UpdatedAt: now);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<PromptDetailDto>(detail, true, null),
            ArcanumJsonContext.Default.ApiResponsePromptDetailDto));

        CliTestResult result = RunCommand(handler, ["prompt", "show", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/prompts/{SampleId:D}", request.RequestUri!.AbsolutePath);

    }

    [Fact]
    public void Prompt_render_posts_parameters()
    {

        PromptRenderResultDto rendered = new("Hello Ada", 3);

        RecordingHandler handler = new(_ => CreateResponse(
            new ApiResponse<PromptRenderResultDto>(rendered, true, null),
            ArcanumJsonContext.Default.ApiResponsePromptRenderResultDto));

        CliTestResult result = RunCommand(handler, ["prompt", "render", SampleId.ToString(), "--param", "name=Ada"]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal($"/api/prompts/{SampleId:D}/render", request.RequestUri!.AbsolutePath);

        string body = ReadBody(request);

        Assert.Contains("\"name\":\"Ada\"", body, StringComparison.Ordinal);

    }

    [Fact]
    public void Prompt_render_rejects_malformed_param_without_calling_api()
    {

        RecordingHandler handler = new();

        CliTestResult result = RunCommand(handler, ["prompt", "render", SampleId.ToString(), "--param", "no-equals-sign"]);

        Assert.Equal(1, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public void Prompt_delete_binds_id_and_handles_no_content()
    {

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        CliTestResult result = RunCommand(handler, ["prompt", "delete", SampleId.ToString()]);

        Assert.Equal(0, result.ExitCode);

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Delete, request.Method);

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
