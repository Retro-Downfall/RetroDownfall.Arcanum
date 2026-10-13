using System.Net;

using System.Reflection;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerOwnedHttpClientTests
{
    [Fact]
    public async Task The_manager_owns_one_client_with_no_overall_deadline_and_disposes_it_with_itself()
    {
        ProbeHandler primary = new();

        McpConnectionManager manager = McpConnectionManagerHarness.Create(httpHandler: primary, managerOwnsHttpHandler: true);

        HttpClient client = Assert.IsType<HttpClient>(typeof(McpConnectionManager)
            .GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);

        Assert.False(primary.Disposed);

        await manager.DisposeAsync();

        Assert.True(primary.Disposed, "Disposing the manager must dispose the owned client and its handler chain.");
    }

    [Fact]
    public async Task A_failed_HTTP_start_leaves_the_shared_client_usable_for_the_next_start()
    {
        ProbeHandler primary = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(httpHandler: primary, managerOwnsHttpHandler: true);

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
                {
                    // A public address literal passes the egress policy without any DNS lookup.
                    ["first"] = new() { Url = "https://93.184.216.34/mcp" },
                    ["second"] = new() { Url = "https://93.184.216.35/mcp" },
                },
            },
            scopeWorkingDirectory: null,
            CancellationToken.None);

        Result first = await manager.StartAsync("first", workingDirectory: null);

        Assert.True(first.IsFailure);

        Assert.Equal("Mcp.StartFailed", first.Error.Code);

        int afterFirst = primary.Requests;

        Assert.True(afterFirst > 0, "The start never reached the owned HTTP client.");

        // The client is shared by every transport, so one session's teardown must not dispose it.
        Assert.False(primary.Disposed);

        Result second = await manager.StartAsync("second", workingDirectory: null);

        Assert.True(second.IsFailure);

        Assert.Equal("Mcp.StartFailed", second.Error.Code);

        Assert.True(primary.Requests > afterFirst, "The second start did not reach the shared client.");

        Assert.False(primary.Disposed);
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
