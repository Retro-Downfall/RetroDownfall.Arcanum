using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LongRestOwnedTransportLoopbackTests
{
    [Fact]
    public async Task Every_SDK_call_is_its_own_request_on_its_own_connection_even_inside_one_scope()
    {
        await using KeepAliveProvider server = new();

        using ServiceProvider services = Services();

        using IServiceScope scope = services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        await Chat(factory, server.Endpoint, deadline.Token);

        Assert.Equal(1, server.RequestCount);

        Assert.Equal(1, server.ConnectionCount);

        await Embedding(factory, server.Endpoint, deadline.Token);

        // One connection per send: the second call never rides the first call's pooled connection.
        Assert.Equal(2, server.RequestCount);

        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task A_provider_that_drops_reused_connections_sees_exactly_one_request_per_SDK_call()
    {
        // The stale keep-alive provider: it answers the first request on a connection and then reads, and drops, any request that
        // arrives on a connection it has already served. A pooled-connection resend would show up here as an extra request.
        await using KeepAliveProvider server = new(dropsReusedConnections: true);

        using ServiceProvider services = Services();

        using IServiceScope scope = services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        await Chat(factory, server.Endpoint, deadline.Token);

        await Embedding(factory, server.Endpoint, deadline.Token);

        await Chat(factory, server.Endpoint, deadline.Token);

        Assert.Equal(3, server.RequestCount);

        Assert.Equal(3, server.ConnectionCount);
    }

    [Fact]
    public async Task A_provider_that_drops_a_fresh_connection_surfaces_as_one_request_and_an_SDK_failure_never_a_resend()
    {
        await using KeepAliveProvider server = new(dropsEveryRequest: true);

        using ServiceProvider services = Services();

        using IServiceScope scope = services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        var resolved = await factory.ResolveChatAsync(Selection(server.Endpoint, false), deadline.Token);

        Assert.True(resolved.IsSuccess);

        using ChatClientLease lease = resolved.Value;

        await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        Assert.Equal(1, server.RequestCount);

        Assert.Equal(1, server.ConnectionCount);
    }

    [Theory]
    [InlineData(500, ErrorCodes.LongRest.ProviderUnavailable)]
    [InlineData(503, ErrorCodes.LongRest.ProviderUnavailable)]
    [InlineData(429, ErrorCodes.LongRest.ProviderUnavailable)]
    [InlineData(408, ErrorCodes.LongRest.ProviderUnavailable)]
    [InlineData(400, ErrorCodes.LongRest.ProviderRejected)]
    [InlineData(401, ErrorCodes.LongRest.ProviderRejected)]
    [InlineData(404, ErrorCodes.LongRest.ProviderRejected)]
    [InlineData(302, ErrorCodes.LongRest.ProviderRejected)]
    public async Task An_error_status_is_typed_from_the_SDK_exception_and_the_typed_failure_carries_no_provider_body(int status, string expected)
    {
        await using KeepAliveProvider server = new(status: status);

        using ServiceProvider services = Services();

        using IServiceScope scope = services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        var resolved = await factory.ResolveChatAsync(Selection(server.Endpoint, false), deadline.Token);

        using ChatClientLease lease = resolved.Value;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        Error typed = LongRestProviderFailure.Classify(failure);

        Assert.Equal(expected, typed.Code);

        // The SDK's own exception may carry the provider's answer; the typed failure the executor returns never does.
        Assert.DoesNotContain("secret-provider-body", typed.Message, StringComparison.Ordinal);

        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task A_closed_port_is_unreachable_because_no_request_byte_could_leave()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);

        probe.Start();

        int port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        using ServiceProvider services = Services();

        using IServiceScope scope = services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        var resolved = await factory.ResolveChatAsync(Selection($"http://127.0.0.1:{port}/v1", false), deadline.Token);

        using ChatClientLease lease = resolved.Value;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, LongRestProviderFailure.Classify(failure).Code);
    }

    // The production egress guard is what turns a name-resolution, address-policy or connect failure into the exception the runtime
    // hands the SDK. The guard drops the resolver's SocketException, so these cases are pinned through the real guard and the real
    // SDK rather than through a hand-built exception: each ends before any connection exists, so no request byte could leave.
    [Theory]
    [InlineData("unresolvable")]
    [InlineData("blocked-address")]
    [InlineData("no-addresses")]
    public async Task A_resolution_or_address_policy_failure_in_the_real_egress_guard_is_unreachable_because_no_request_byte_could_leave(string variant)
    {
        FakeDnsResolver dns = new();

        dns.Add("blocked.example", IPAddress.Parse("169.254.169.254"));

        dns.Add("empty.example");

        string host = variant switch
        {
            "blocked-address" => "blocked.example",
            "no-addresses" => "empty.example",
            _ => "no-such-host.example",
        };

        using LongRestProviderClientFactory factory = new(new Keys(), OutboundUrlGuard.CreateProviderEgressHandler(dns));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        var resolved = await factory.ResolveChatAsync(Selection($"http://{host}/v1", false), deadline.Token);

        using ChatClientLease lease = resolved.Value;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, LongRestProviderFailure.Classify(failure).Code);
    }

    [Fact]
    public async Task A_connect_failure_to_a_resolved_address_in_the_real_egress_guard_is_unreachable()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);

        probe.Start();

        int port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        FakeDnsResolver dns = new();

        dns.Add("refusing.example", IPAddress.Loopback);

        using LongRestProviderClientFactory factory = new(new Keys(), OutboundUrlGuard.CreateProviderEgressHandler(dns));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        var resolved = await factory.ResolveChatAsync(Selection($"http://refusing.example:{port}/v1", false), deadline.Token);

        using ChatClientLease lease = resolved.Value;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnreachable, LongRestProviderFailure.Classify(failure).Code);
    }

    [Fact]
    public async Task A_connection_dropped_after_the_request_was_read_is_a_transient_failure_that_may_have_been_served()
    {
        await using KeepAliveProvider server = new(dropsEveryRequest: true);

        using ServiceProvider services = Services();

        using IServiceScope scope = services.CreateScope();

        LongRestProviderClientFactory factory = scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));

        var resolved = await factory.ResolveChatAsync(Selection(server.Endpoint, false), deadline.Token);

        using ChatClientLease lease = resolved.Value;

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: deadline.Token));

        Assert.Equal(ErrorCodes.LongRest.ProviderUnavailable, LongRestProviderFailure.Classify(failure).Code);
    }

    [Fact]
    public async Task A_new_scope_uses_its_own_connection_after_the_prior_call_has_joined()
    {
        await using KeepAliveProvider server = new();

        using ServiceProvider services = Services();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        using (IServiceScope first = services.CreateScope())
        {
            await Chat(first.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>(), server.Endpoint, deadline.Token);

            Assert.Equal(1, server.RequestCount);

            Assert.Equal(1, server.ConnectionCount);
        }

        using (IServiceScope second = services.CreateScope())
        {
            await Embedding(second.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>(), server.Endpoint, deadline.Token);

            Assert.Equal(2, server.RequestCount);

            Assert.Equal(2, server.ConnectionCount);
        }
    }

    [Fact]
    public async Task The_connection_closes_once_the_SDK_call_has_joined_without_waiting_for_scope_disposal()
    {
        await using KeepAliveProvider server = new();

        using ServiceProvider services = Services();

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

        using IServiceScope scope = services.CreateScope();

        await Chat(scope.ServiceProvider.GetRequiredService<LongRestProviderClientFactory>(), server.Endpoint, deadline.Token);

        Assert.Equal(1, server.RequestCount);

        Assert.Equal(1, server.ConnectionCount);

        // One finite absence-of-close guard; this is not a DNS timing claim. The scope is still alive here.
        Task terminal = await Task.WhenAny(server.FirstConnectionClosed, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(server.FirstConnectionClosed, terminal);

        await server.FirstConnectionClosed;
    }

    private static ServiceProvider Services()
    {
        ServiceCollection services = new();

        services.AddHttpClient("LongRestProvider", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => OutboundUrlGuard.CreateProviderEgressHandler());

        services.AddSingleton<IProviderApiKeyResolver>(new Keys());

        services.AddScoped<LongRestProviderClientFactory>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static LongRestProviderSelection Selection(string endpoint, bool embedding) => new(
        new ProviderSettings { Name = "loopback", Type = AiProviderKind.OpenAICompatible, Endpoint = endpoint },
        embedding ? "embedding" : "model", 64, new ModelPricingEntry());

    private static async Task Chat(LongRestProviderClientFactory factory, string endpoint, CancellationToken token)
    {
        var resolved = await factory.ResolveChatAsync(Selection(endpoint, false), token);

        Assert.True(resolved.IsSuccess);

        using ChatClientLease lease = resolved.Value;

        ChatResponse response = await lease.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "probe")], cancellationToken: token);

        Assert.Equal("ok", response.Text);
    }

    private static async Task Embedding(LongRestProviderClientFactory factory, string endpoint, CancellationToken token)
    {
        var resolved = await factory.ResolveEmbeddingAsync(Selection(endpoint, true), token);

        Assert.True(resolved.IsSuccess);

        using EmbeddingGeneratorLease lease = resolved.Value;

        var response = await lease.Generator.GenerateAsync(["probe"], new EmbeddingGenerationOptions { Dimensions = 64 }, token);

        Assert.Single(response);

        Assert.Equal(64, response[0].Vector.Length);
    }

    private sealed class Keys : IProviderApiKeyResolver
    {
        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken) => Task.FromResult<string?>("test-key");
    }

    private sealed class KeepAliveProvider : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        private readonly CancellationTokenSource _stopping = new();

        private readonly List<TcpClient> _clients = [];

        private readonly List<Task> _connections = [];

        private readonly TaskCompletionSource _firstClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Task _accepting;

        private readonly bool _dropsReusedConnections;

        private readonly bool _dropsEveryRequest;

        private int _connectionCount;

        private int _requestCount;

        private readonly int _status;

        internal KeepAliveProvider(bool dropsReusedConnections = false, bool dropsEveryRequest = false, int status = 200)
        {
            _status = status;

            _dropsReusedConnections = dropsReusedConnections;

            _dropsEveryRequest = dropsEveryRequest;

            _listener.Start();

            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

            _accepting = Accept();
        }

        internal string Endpoint { get; }

        internal int ConnectionCount => Volatile.Read(ref _connectionCount);

        internal int RequestCount => Volatile.Read(ref _requestCount);

        internal Task FirstConnectionClosed => _firstClosed.Task;

        private async Task Accept()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);

                    int ordinal = Interlocked.Increment(ref _connectionCount);

                    lock (_clients)
                    {
                        _clients.Add(client);

                        _connections.Add(Serve(client, ordinal));
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

                int onThisConnection = 0;

                while (await Request(wire, _stopping.Token) is { } path)
                {
                    Interlocked.Increment(ref _requestCount);

                    onThisConnection++;

                    // A full request has been read; the stale provider now closes without any response.
                    if (_dropsEveryRequest || _dropsReusedConnections && onThisConnection > 1) break;

                    string body = _status != 200 ? "{\"error\":{\"message\":\"secret-provider-body\"}}" : path.EndsWith("/embeddings", StringComparison.Ordinal)
                        ? "{\"object\":\"list\",\"model\":\"embedding\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":\"" + Convert.ToBase64String(new byte[64 * sizeof(float)]) + "\"}],\"usage\":{\"prompt_tokens\":1,\"total_tokens\":1}}"
                        : "{\"id\":\"loopback\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"model\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

                    byte[] payload = Encoding.UTF8.GetBytes(body);

                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {_status} Test\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: keep-alive\r\n\r\n");

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

                if (ordinal == 1) _firstClosed.TrySetResult();
            }
        }

        private static async Task<string?> Request(NetworkStream wire, CancellationToken token)
        {
            List<byte> header = [];

            byte[] next = new byte[1];

            while (header.Count < 32768)
            {
                int read = await wire.ReadAsync(next, token);

                if (read == 0)
                {
                    Assert.Empty(header);

                    return null;
                }

                header.Add(next[0]);

                if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
            }

            Assert.True(header.Count < 32768);

            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            string? length = lines.SingleOrDefault(static line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

            if (length is not null)
            {
                int contentLength = int.Parse(length[(length.IndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

                Assert.InRange(contentLength, 0, 32768);

                await wire.ReadExactlyAsync(new byte[contentLength], token);
            }
            else
            {
                Assert.Contains(lines, static line => line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase));

                int total = 0;

                while (true)
                {
                    string line = await Line(wire, token);

                    int size = int.Parse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

                    Assert.InRange(size, 0, 32768 - total);

                    if (size == 0)
                    {
                        Assert.Empty(await Line(wire, token));

                        break;
                    }

                    total += size;

                    await wire.ReadExactlyAsync(new byte[size], token);

                    Assert.Empty(await Line(wire, token));
                }
            }

            return lines[0].Split(' ')[1];
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
