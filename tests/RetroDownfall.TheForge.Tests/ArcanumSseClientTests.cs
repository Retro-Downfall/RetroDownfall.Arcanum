using System.Net;

using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.TheForge.Core.Models;

using RetroDownfall.TheForge.Core.Services;

using RetroDownfall.TheForge.Ux.Services;

using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ArcanumSseClientTests
{

    [Fact]
    public async Task StreamSessionEntriesAsync_UsesEntryIdAsResumeCursor()
    {

        Guid sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        Guid entryId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        RecordingSseHandler handler = new();

        ArcanumApiClient apiClient = new(
            new StaticHttpClientFactory(handler),
            new StaticOptionsMonitor(new TheForgeSettings
            {

                BaseUrl = "http://localhost:5001",

            }),
            new StaticApiKeyProvider(),
            NullLogger<ArcanumApiClient>.Instance);

        ArcanumSseClient sseClient = new(apiClient, NullLogger<ArcanumSseClient>.Instance);

        await foreach (var _ in sseClient.StreamSessionEntriesAsync(
                           sessionId,
                           entryId,
                           CancellationToken.None))
        {

        }

        Assert.Equal(
            $"http://localhost:5001/api/sessions/{sessionId:D}/stream?since={entryId:D}",
            handler.RequestUri?.AbsoluteUri);

    }

    private sealed class RecordingSseHandler : HttpMessageHandler
    {

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            RequestUri = request.RequestUri;

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {

                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),

            };

            return Task.FromResult(response);

        }

    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001"),

            };

    }

    private sealed class StaticApiKeyProvider : ITheForgeApiKeyProvider
    {

        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-key");

        public Task PersistPastedKeyAsync(string apiKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {

        }

    }

    private sealed class StaticOptionsMonitor(TheForgeSettings settings) : IOptionsMonitor<TheForgeSettings>
    {

        public TheForgeSettings CurrentValue => settings;

        public TheForgeSettings Get(string? name) => settings;

        public IDisposable? OnChange(Action<TheForgeSettings, string?> listener) => null;

    }

}
