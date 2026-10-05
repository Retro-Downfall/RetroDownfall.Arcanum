using System.Net;

using System.Net.Sockets;

using System.Text;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Pins the response-headers deadline against the real transport. The deadline tests that use stub
/// handlers prove the sender disposes its timer with the headers; these prove a real
/// <see cref="SocketsHttpHandler"/> does not cut a <c>ResponseHeadersRead</c> body short when the
/// deadline's interval passes, or even when the send token itself is cancelled, while the body is
/// still arriving. A stub handler cannot show either.
/// </summary>
public sealed class ArcanumHeadersDeadlineTransportTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The path the sender takes: the deadline interval passes with the body half delivered, and the
    /// body is read to the end.
    /// </summary>
    [Fact]
    public async Task A_body_still_arriving_when_the_deadline_interval_passes_is_read_to_the_end_on_a_real_transport()
    {
        await using SlowBodyServer server = new();

        using HttpClient client = server.CreateClient();

        using ArcanumApiCredentialLease credentials = ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        ManualTimerTimeProvider clock = new();

        TimeSpan deadline = TimeSpan.FromMinutes(5);

        using ArcanumAuthenticatedHttpResponse sent = await ArcanumAuthenticatedHttpSender
            .SendAsync(
                client,
                credentials,
                static () => new HttpRequestMessage(HttpMethod.Get, "/slow-body"),
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                ArcanumAuthenticatedHttpSender.PresenceProbeTimeout,
                CancellationToken.None,
                deadline,
                clock)
            .WaitAsync(HangGuard);

        Assert.True(sent.IsAuthenticated);

        await using Stream body = await sent.Response!.Content.ReadAsStreamAsync().WaitAsync(HangGuard);

        Assert.Equal("hello", await ReadFiveAsync(body));

        // The deadline's whole interval passes with the body half delivered.
        clock.Advance(deadline + deadline);

        Assert.Equal(0, clock.ActiveTimers);

        server.ReleaseSecondHalf();

        Assert.Equal("world", await ReadFiveAsync(body));

        await server.Completed.WaitAsync(HangGuard);
    }

    /// <summary>
    /// The framework contract the sender leans on, with the sender taken out: a token handed to
    /// <c>SendAsync</c> with <c>ResponseHeadersRead</c> is a headers-phase token, so cancelling it after
    /// the headers arrived leaves the body readable. If this ever fails, disposing the deadline source
    /// with the headers is the only thing keeping a lapsed deadline off the body.
    /// </summary>
    [Fact]
    public async Task A_send_token_cancelled_after_the_headers_arrived_does_not_cut_the_body_on_a_real_transport()
    {
        await using SlowBodyServer server = new();

        using HttpClient client = server.CreateClient();

        using CancellationTokenSource sendToken = new();

        using HttpResponseMessage response = await client
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "/slow-body"),
                HttpCompletionOption.ResponseHeadersRead,
                sendToken.Token)
            .WaitAsync(HangGuard);

        await using Stream body = await response.Content.ReadAsStreamAsync().WaitAsync(HangGuard);

        Assert.Equal("hello", await ReadFiveAsync(body));

        await sendToken.CancelAsync();

        server.ReleaseSecondHalf();

        Assert.Equal("world", await ReadFiveAsync(body));

        await server.Completed.WaitAsync(HangGuard);
    }

    private static async Task<string> ReadFiveAsync(Stream body)
    {
        byte[] bytes = new byte[5];

        await body.ReadExactlyAsync(bytes).AsTask().WaitAsync(HangGuard);

        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// A loopback HTTP/1.1 server that answers one request with a ten-byte body, sends the first five
    /// bytes with the headers and the last five only when the test releases them.
    /// </summary>
    private sealed class SlowBodyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly TaskCompletionSource _releaseSecondHalf = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowBodyServer()
        {
            _listener.Start();

            Completed = Task.Run(ServeAsync);
        }

        /// <summary>Completes once the second half has been written, or faults with the server's failure.</summary>
        public Task Completed { get; }

        public void ReleaseSecondHalf() => _releaseSecondHalf.TrySetResult();

        /// <summary>
        /// A client whose connections go to the loopback listener whatever authority the request names,
        /// so the request can carry the authority the credential lease was verified for.
        /// </summary>
        public HttpClient CreateClient()
        {
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            SocketsHttpHandler handler = new()
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (_, cancellationToken) =>
                {
                    Socket socket = new(SocketType.Stream, ProtocolType.Tcp);

                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);

                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();

                        throw;
                    }
                },
            };

            return new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public async ValueTask DisposeAsync()
        {
            _releaseSecondHalf.TrySetResult();

            _listener.Stop();

            try
            {
                await Completed.WaitAsync(HangGuard);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                // The test already failed or finished; a torn-down connection is not a second failure.
            }
        }

        private async Task ServeAsync()
        {
            using TcpClient peer = await _listener.AcceptTcpClientAsync().WaitAsync(HangGuard);

            NetworkStream network = peer.GetStream();

            await ReadRequestHeadAsync(network);

            await network.WriteAsync(
                Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 10\r\nConnection: close\r\n\r\nhello"));

            await network.FlushAsync();

            await _releaseSecondHalf.Task.WaitAsync(HangGuard);

            await network.WriteAsync(Encoding.ASCII.GetBytes("world"));

            await network.FlushAsync();
        }

        private static async Task ReadRequestHeadAsync(NetworkStream network)
        {
            byte[] buffer = new byte[4096];

            int total = 0;

            while (total < buffer.Length)
            {
                int read = await network.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(HangGuard);

                if (read == 0)
                {
                    return;
                }

                total += read;

                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return;
                }
            }
        }
    }
}
