using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// <c>the-forge.json</c> is live-editable and only the Setup Wizard validates BaseUrl, so a hand edit can
/// reach <c>CreateClientAsync</c> with a value the BCL rejects. Those faults must arrive as envelopes.
/// </summary>
public sealed class ArcanumApiClientConfigurationTests
{
    [Fact]
    public async Task GetAsync_empty_base_url_returns_config_failure_envelope()
    {
        ArcanumApiClient client = CreateClient(baseUrl: string.Empty);

        ApiResponse<HealthReportDto>? result = await client.GetAsync(
            "/api/health",
            TheForgeJsonContext.Default.ApiResponseHealthReportDto,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Config.InvalidBaseUrl", result.Error?.Code);
    }

    [Fact]
    public async Task GetAsync_base_url_without_scheme_returns_config_failure_envelope()
    {
        ArcanumApiClient client = CreateClient(baseUrl: "localhost:5001");

        ApiResponse<HealthReportDto>? result = await client.GetAsync(
            "/api/health",
            TheForgeJsonContext.Default.ApiResponseHealthReportDto,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Config.InvalidBaseUrl", result.Error?.Code);
    }

    [Fact]
    public async Task GetAsync_api_key_with_embedded_newline_returns_config_failure_envelope()
    {
        ArcanumApiClient client = CreateClient(apiKey: "line-one\nline-two");

        ApiResponse<HealthReportDto>? result = await client.GetAsync(
            "/api/health",
            TheForgeJsonContext.Default.ApiResponseHealthReportDto,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.IsSuccess);
        Assert.Equal("Config.InvalidApiKey", result.Error?.Code);
    }

    [Fact]
    public async Task DeleteNoContentAsync_empty_base_url_returns_false_instead_of_throwing()
    {
        ArcanumApiClient client = CreateClient(baseUrl: string.Empty);

        DeleteOutcome result = await client.DeleteNoContentAsync("/api/campaigns/test", CancellationToken.None);

        Assert.False(result.Success);

        Assert.Equal("Config.InvalidBaseUrl", result.ErrorCode);
    }

    [Fact]
    public async Task GetSseAsync_empty_base_url_yields_nothing_instead_of_throwing()
    {
        ArcanumApiClient client = CreateClient(baseUrl: string.Empty);

        List<SseEvent> events = [];

        await foreach (SseEvent sseEvent in client.GetSseAsync("/api/sessions/stream", CancellationToken.None))
        {
            events.Add(sseEvent);
        }

        Assert.Empty(events);
    }

    private static ArcanumApiClient CreateClient(
        string baseUrl = "http://localhost:5001",
        string apiKey = "test-key") =>
        new(
            new StaticHttpClientFactory(new EmptyOkHandler()),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings
            {
                BaseUrl = baseUrl,
            }),
            new StaticApiKeyProvider(apiKey),
            NullLogger<ArcanumApiClient>.Instance);

    private sealed class StaticApiKeyProvider(string apiKey) : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(apiKey);

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false);
    }

    private sealed class EmptyOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty),
            });
    }
}
