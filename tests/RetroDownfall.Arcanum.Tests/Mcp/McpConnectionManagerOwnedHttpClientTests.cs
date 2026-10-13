using System.Net;

using ModelContextProtocol.Client;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerOwnedHttpClientTests
{
    [Fact]
    public async Task A_failed_HTTP_start_disposes_the_client_its_transport_owned()
    {
        ProbeHandler primary = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        // The manager builds its own client over this primary handler; the response bound still wraps it.
        manager.PrimaryHttpHandlerForTests = () => primary;

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    // A public address literal passes the egress policy without any DNS lookup.
                    ["remote"] = new() { Url = "https://93.184.216.34/mcp" },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result started = await manager.StartAsync("remote", workingDirectory: null);

        Assert.True(started.IsFailure);

        Assert.Equal("Mcp.StartFailed", started.Error.Code);

        Assert.True(primary.Requests > 0, "The start never reached the owned HTTP client.");

        Assert.True(primary.Disposed, "The failed start left the owned client and its handler chain undisposed.");
    }

    [Fact]
    public async Task A_session_wrapper_releases_its_owned_resource_even_when_the_handshake_never_ran()
    {
        int disposals = 0;

        using HttpClient transportClient = new();

        HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("https://93.184.216.34/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            transportClient,
            loggerFactory: null,
            ownsHttpClient: false);

        SdkMcpClientWrapper wrapper = new(
            transport,
            new McpClientOptions(),
            initializationTimeout: TimeSpan.FromSeconds(30),
            toolOutputCapBytes: 65536,
            maxToolsTotalBytes: 1_048_576,
            elicitationSink: new McpElicitationSink(),
            ownedResource: new DisposeCounter(() => disposals++));

        Assert.Equal(0, disposals);

        await wrapper.DisposeAsync();

        await wrapper.DisposeAsync();

        Assert.Equal(1, disposals);
    }

    private sealed class DisposeCounter(Action disposed) : IDisposable
    {
        public void Dispose() => disposed();
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _requests);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;

            base.Dispose(disposing);
        }
    }
}
