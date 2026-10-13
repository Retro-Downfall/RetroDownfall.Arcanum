using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

// The real factory, the real pinned provider egress handler and a real loopback socket: no seam. What the
// provider observes is exactly what the SDK was asked to send.
public sealed class CampaignRollupOwnedTransportLoopbackTests
{
    [Fact]
    public async Task One_SDK_call_is_one_request_on_one_connection()
    {
        await using LoopbackProvider server = new();

        using CampaignRollupProviderClientFactory factory = Factory(server.Endpoint);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        await Call(factory, deadline.Token);

        Assert.Equal(1, server.RequestCount);

        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task Requests_equal_SDK_calls_across_leases_on_the_scope_connection()
    {
        await using LoopbackProvider server = new();

        using CampaignRollupProviderClientFactory factory = Factory(server.Endpoint);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        for (int call = 1; call <= 3; call++)
        {
            await Call(factory, deadline.Token);

            Assert.Equal(call, server.RequestCount);
        }

        // Disposing a lease leaves the scope client, and the connection it holds, open for the next lease.
        Assert.Equal(1, server.ConnectionCount);

        Assert.False(server.Closed(1).IsCompleted);
    }

    [Fact]
    public async Task Two_calls_on_one_lease_are_two_requests_on_the_lease_connection()
    {
        await using LoopbackProvider server = new();

        using CampaignRollupProviderClientFactory factory = Factory(server.Endpoint);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, deadline.Token);

        using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

        for (int call = 1; call <= 2; call++)
        {
            ChatResponse response = await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token);

            Assert.Equal("ok", response.Text);

            Assert.Equal(call, server.RequestCount);
        }

        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task Scope_disposal_closes_the_keep_alive_connection_after_the_SDK_call_has_joined()
    {
        await using LoopbackProvider server = new();

        using ServiceProvider services = Services(server.Endpoint);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        using (IServiceScope scope = services.CreateScope())
        {
            ICampaignRollupProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<ICampaignRollupProviderClientFactory>();

            Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, deadline.Token);

            using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

            ChatResponse response = await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token);

            Assert.Equal("ok", response.Text);

            Assert.Equal(1, server.RequestCount);

            Assert.False(server.Closed(1).IsCompleted);
        }

        // One finite absence-of-disposal guard; this is not a DNS timing claim.
        Task closed = await Task.WhenAny(server.Closed(1), Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(server.Closed(1), closed);
    }

    // Production registers no named client for the factory: only its two collaborators are needed.
    private static ServiceProvider Services(string endpoint)
    {
        ServiceCollection services = new();

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(Settings(endpoint)));

        services.AddSingleton<IProviderApiKeyResolver>(new Keys());

        services.AddScoped<ICampaignRollupProviderClientFactory, CampaignRollupProviderClientFactory>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Theory]
    [InlineData("http://169.254.169.254/v1")]
    [InlineData("http://[fe80::1]/v1")]
    public async Task The_pinned_egress_handler_refuses_a_link_local_provider_endpoint(string endpoint)
    {
        using CampaignRollupProviderClientFactory factory = Factory(endpoint);

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, deadline.Token);

        using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException refused)
            {
                Assert.Contains("not permitted", refused.Message, StringComparison.Ordinal);

                return;
            }
        }

        Assert.Fail("The link-local endpoint was not refused by the egress handler: " + failure);
    }

    private static CampaignRollupProviderClientFactory Factory(string endpoint) => new(
        new TestOptionsMonitor<ArcanumSettings>(Settings(endpoint)),
        new Keys());

    private static ArcanumSettings Settings(string endpoint) => new()
    {
        DefaultModel = "model",
        Providers =
        [
            new ProviderSettings
            {
                Name = "loopback",
                Type = AiProviderKind.OpenAICompatible,
                Endpoint = endpoint,
                Models = ["model"],
            },
        ],
    };

    private static async Task Call(CampaignRollupProviderClientFactory factory, CancellationToken token)
    {
        Result<ChatClientLease?> resolved = await factory.ResolveClientAsync(null, token);

        Assert.True(resolved.IsSuccess);

        using ChatClientLease lease = Assert.IsType<ChatClientLease>(resolved.Value);

        ChatResponse response = await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: token);

        Assert.Equal("ok", response.Text);
    }

    private sealed class Keys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");
    }

    private sealed class LoopbackProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly CancellationTokenSource _stopping = new();

        private readonly List<TcpClient> _clients = [];

        private readonly List<Task> _connections = [];

        private readonly List<TaskCompletionSource> _closed = [];

        private readonly Task _accepting;

        private int _requestCount;

        internal LoopbackProvider()
        {
            _listener.Start();

            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

            _accepting = Accept();
        }

        internal string Endpoint { get; }

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal int ConnectionCount
        {
            get
            {
                lock (_clients)
                {
                    return _clients.Count;
                }
            }
        }

        // The one-based connection ordinal's closure, creatable before the connection exists.
        internal Task Closed(int ordinal)
        {
            lock (_clients)
            {
                while (_closed.Count < ordinal)
                {
                    _closed.Add(new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return _closed[ordinal - 1].Task;
            }
        }

        private async Task Accept()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);

                    lock (_clients)
                    {
                        _clients.Add(client);

                        _connections.Add(Serve(client, _clients.Count));
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            catch (InvalidOperationException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client, int ordinal)
        {
            try
            {
                using NetworkStream wire = client.GetStream();

                while (await Request(wire, _stopping.Token))
                {
                    Interlocked.Increment(ref _requestCount);

                    byte[] payload = Encoding.UTF8.GetBytes("{\"id\":\"loopback\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}");

                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: keep-alive\r\n\r\n");

                    await wire.WriteAsync(header, _stopping.Token);

                    await wire.WriteAsync(payload, _stopping.Token);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (IOException) { }
            catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
            finally
            {
                client.Dispose();

                _ = Closed(ordinal);

                lock (_clients)
                {
                    _closed[ordinal - 1].TrySetResult();
                }
            }
        }

        private static async Task<bool> Request(NetworkStream wire, CancellationToken token)
        {
            List<byte> header = [];

            byte[] next = new byte[1];

            while (header.Count < 32768)
            {
                int read = await wire.ReadAsync(next, token);

                if (read == 0)
                {
                    return false;
                }

                header.Add(next[0]);

                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }

            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            string? length = lines.SingleOrDefault(static line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

            if (length is not null)
            {
                int contentLength = int.Parse(length[(length.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

                await wire.ReadExactlyAsync(new byte[contentLength], token);

                return true;
            }

            // A chunked request body, read to its terminating zero-length chunk.
            while (true)
            {
                string line = await Line(wire, token);

                int size = int.Parse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

                if (size == 0)
                {
                    _ = await Line(wire, token);

                    return true;
                }

                await wire.ReadExactlyAsync(new byte[size], token);

                _ = await Line(wire, token);
            }
        }

        private static async Task<string> Line(NetworkStream wire, CancellationToken token)
        {
            List<byte> bytes = [];

            byte[] next = new byte[1];

            while (bytes.Count < 1024)
            {
                await wire.ReadExactlyAsync(next, token);

                bytes.Add(next[0]);

                if (bytes.Count >= 2 && bytes[^2] == 13 && bytes[^1] == 10)
                    return Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 2);
            }

            throw new InvalidOperationException("The loopback request chunk line exceeded its test bound.");
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();

            _listener.Stop();

            await _accepting;

            Task[] connections;

            lock (_clients)
            {
                foreach (TcpClient client in _clients) client.Dispose();

                connections = [.. _connections];
            }

            await Task.WhenAll(connections);

            _stopping.Dispose();
        }
    }
}
