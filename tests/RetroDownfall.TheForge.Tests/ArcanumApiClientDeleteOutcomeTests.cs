using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// <c>DELETE</c> routes that answer <c>204 No Content</c> still fail in more than one way. A single
/// boolean forces every data source to invent one reason, which is how a connection refusal, a 403,
/// and a 500 all end up on screen as "not found".
/// </summary>
public sealed class ArcanumApiClientDeleteOutcomeTests
{

    [Fact]
    public async Task DeleteNoContentAsync_no_content_succeeds()
    {

        ArcanumApiClient client = CreateClient(new StatusHandler(HttpStatusCode.NoContent));

        DeleteOutcome outcome = await client.DeleteNoContentAsync("/api/campaigns/test", CancellationToken.None);

        Assert.True(outcome.Success);

        Assert.Null(outcome.ErrorCode);

    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Http.404")]
    [InlineData(HttpStatusCode.Forbidden, "Http.403")]
    [InlineData(HttpStatusCode.Conflict, "Http.409")]
    [InlineData(HttpStatusCode.InternalServerError, "Http.500")]
    public async Task DeleteNoContentAsync_reports_the_status_it_actually_got(HttpStatusCode status, string expectedCode)
    {

        ArcanumApiClient client = CreateClient(new StatusHandler(status));

        DeleteOutcome outcome = await client.DeleteNoContentAsync("/api/campaigns/test", CancellationToken.None);

        Assert.False(outcome.Success);

        Assert.Equal(expectedCode, outcome.ErrorCode);

    }

    [Fact]
    public async Task DeleteNoContentAsync_unreachable_host_reports_a_connection_failure()
    {

        ArcanumApiClient client = CreateClient(new RefusingHandler());

        DeleteOutcome outcome = await client.DeleteNoContentAsync("/api/campaigns/test", CancellationToken.None);

        Assert.False(outcome.Success);

        Assert.Equal("Connection.Failed", outcome.ErrorCode);

    }

    [Fact]
    public async Task DeleteNoContentAsync_request_timeout_reports_a_timeout()
    {

        ArcanumApiClient client = CreateClient(new TimingOutHandler());

        DeleteOutcome outcome = await client.DeleteNoContentAsync("/api/campaigns/test", CancellationToken.None);

        Assert.False(outcome.Success);

        Assert.Equal("Connection.Timeout", outcome.ErrorCode);

    }

    [Fact]
    public async Task DeleteNoContentAsync_malformed_base_url_reports_the_configuration_code()
    {

        ArcanumApiClient client = CreateClient(new StatusHandler(HttpStatusCode.NoContent), baseUrl: string.Empty);

        DeleteOutcome outcome = await client.DeleteNoContentAsync("/api/campaigns/test", CancellationToken.None);

        Assert.False(outcome.Success);

        Assert.Equal(ArcanumApiClient.InvalidBaseUrlCode, outcome.ErrorCode);

    }

    private static ArcanumApiClient CreateClient(
        HttpMessageHandler handler,
        string baseUrl = "http://localhost:5001") =>
        new(
            new StaticHttpClientFactory(handler),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings
            {
                BaseUrl = baseUrl,
            }),
            new StaticApiKeyProvider(),
            NullLogger<ArcanumApiClient>.Instance);

    private sealed class StaticApiKeyProvider : ITheForgeApiKeyProvider
    {

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-key");

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

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));

    }

    private sealed class RefusingHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused."));

    }

    private sealed class TimingOutHandler : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout elapsing.",
                    new TimeoutException()));

    }

}
