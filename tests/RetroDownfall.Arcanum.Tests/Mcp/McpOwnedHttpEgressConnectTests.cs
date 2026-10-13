using System.Net;

using System.Net.Sockets;

using System.Reflection;

using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// The Streamable HTTP transport's client is built by hand now, not resolved from a named factory client, so
/// nothing but these tests notices if its primary handler stops being the untrusted-egress handler. They send
/// a real request at connect time to a real loopback listener: the untrusted handler must refuse it before a
/// socket opens, and the provider egress handler, which allows loopback, must connect to the same kind of
/// listener, so the refusal is proven to come from the handler choice and not from the listener.
/// </summary>
public sealed class McpOwnedHttpEgressConnectTests
{
    private const string BlockedMessage = "Outbound URL targets a loopback, private, or link-local address and is not permitted.";

    [Fact]
    public async Task The_provider_egress_handler_connects_to_a_loopback_listener_so_the_refusals_discriminate()
    {
        await using LoopbackListener listener = new();

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateProviderEgressHandler();

        using HttpClient client = new(handler, disposeHandler: false);

        using HttpResponseMessage response = await client.GetAsync(listener.Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, listener.Accepted);
    }

    [Fact]
    public async Task The_owned_pipeline_primary_refuses_a_loopback_listener_at_connect_time()
    {
        await using LoopbackListener listener = new();

        using HttpMessageHandler pipeline = McpConnectionManager.CreateOwnedHttpPipeline(TimeSpan.FromSeconds(5));

        using HttpClient client = new(pipeline, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(listener.Url));

        Assert.Contains(BlockedMessage, Flatten(failure), StringComparison.Ordinal);

        Assert.Equal(0, listener.Accepted);
    }

    [Fact]
    public async Task The_manager_real_client_refuses_a_loopback_listener_at_connect_time()
    {
        await using LoopbackListener listener = new();

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        HttpClient client = Assert.IsType<HttpClient>(typeof(McpConnectionManager)
            .GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager));

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(listener.Url));

        Assert.Contains(BlockedMessage, Flatten(failure), StringComparison.Ordinal);

        Assert.Equal(0, listener.Accepted);
    }

    [Theory]
    [InlineData("http://10.0.0.1:9/")]
    [InlineData("http://172.16.0.1:9/")]
    [InlineData("http://192.168.0.1:9/")]
    [InlineData("http://169.254.169.254/")]
    public async Task The_owned_pipeline_primary_refuses_private_and_link_local_literals_before_any_socket_opens(string url)
    {
        using HttpMessageHandler pipeline = McpConnectionManager.CreateOwnedHttpPipeline(TimeSpan.FromSeconds(5));

        using HttpClient client = new(pipeline, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url));

        Assert.Contains(BlockedMessage, Flatten(failure), StringComparison.Ordinal);
    }

    private static string Flatten(Exception failure)
    {
        StringBuilder text = new();

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            _ = text.Append(current.Message).Append(' ');
        }

        return text.ToString();
    }

    // A loopback HTTP listener that counts the connections it accepts and answers each request with 200.
    private sealed class LoopbackListener : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly CancellationTokenSource _stopping = new();

        private readonly List<Task> _connections = [];

        private readonly Task _accepting;

        private int _accepted;

        internal LoopbackListener()
        {
            _listener.Start();

            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

            _accepting = Accept();
        }

        internal string Url { get; }

        internal int Accepted => Volatile.Read(ref _accepted);

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();

            _listener.Stop();

            await _accepting;

            Task[] connections;

            lock (_connections)
            {
                connections = [.. _connections];
            }

            await Task.WhenAll(connections);

            _stopping.Dispose();
        }

        private async Task Accept()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);

                    _ = Interlocked.Increment(ref _accepted);

                    lock (_connections)
                    {
                        _connections.Add(Serve(client));
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client)
        {
            try
            {
                using NetworkStream wire = client.GetStream();

                byte[] next = new byte[1];

                int matched = 0;

                while (matched < 4)
                {
                    if (await wire.ReadAsync(next, _stopping.Token) == 0)
                    {
                        return;
                    }

                    matched = next[0] == (matched % 2 == 0 ? 13 : 10) ? matched + 1 : next[0] == 13 ? 1 : 0;
                }

                await wire.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            finally
            {
                client.Dispose();
            }
        }
    }
}
