using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class PresenceEndpointTests
{
    [Fact]
    public async Task Anonymous_database_free_challenge_returns_a_nonce_and_authority_bound_proof()
    {
        byte[] keyDigest = SHA256.HashData("presence-test-key"u8);

        await using WebApplication app = await CreateHostAsync(keyDigest);

        HttpClient client = app.GetTestClient();

        byte[] nonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            ArcanumPresenceProofProtocol.Path);

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.PresenceNonce,
            ArcanumPresenceProofProtocol.Encode(nonce));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        string version = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceVersion));

        string authority = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceAuthority));

        string encodedProof = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceProof));

        string encodedCapability = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceCapability));

        Assert.Equal(ArcanumPresenceProofProtocol.Version, version);
        Assert.Equal("http://localhost:5001", authority);

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                encodedProof,
                ArcanumPresenceProofProtocol.ProofBytes,
                out byte[]? proof));

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                encodedCapability,
                ArcanumPresenceProofProtocol.CapabilityEnvelopeBytes,
                out byte[]? capabilityEnvelope));

        Assert.True(
            ArcanumPresenceProofProtocol.VerifyProof(
                keyDigest,
                nonce,
                authority,
                capabilityEnvelope!,
                proof!));

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecryptCapabilityEnvelope(
                keyDigest,
                nonce,
                authority,
                capabilityEnvelope,
                out byte[]? processCapability));

        Assert.True(
            app.Services
                .GetRequiredService<ArcanumProcessCapabilityService>()
                .IsValidEncoded(
                    ArcanumPresenceProofProtocol.Encode(processCapability)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    public async Task Missing_or_malformed_nonce_is_rejected_without_a_proof(
        string? encodedNonce)
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8));

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            ArcanumPresenceProofProtocol.Path);

        if (encodedNonce is not null)
        {
            _ = request.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceNonce,
                encodedNonce);
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    [Fact]
    public async Task Duplicate_nonce_is_rejected()
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8));

        HttpClient client = app.GetTestClient();

        string nonce = ArcanumPresenceProofProtocol.Encode(
            RandomNumberGenerator.GetBytes(
                ArcanumPresenceProofProtocol.NonceBytes));

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            ArcanumPresenceProofProtocol.Path);

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.PresenceNonce,
            [nonce, nonce]);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    [Fact]
    public async Task Missing_process_digest_is_unavailable_and_never_returns_a_proof()
    {
        await using WebApplication app = await CreateHostAsync(keyDigest: null);

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            ArcanumPresenceProofProtocol.Path);

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.PresenceNonce,
            ArcanumPresenceProofProtocol.Encode(
                RandomNumberGenerator.GetBytes(
                    ArcanumPresenceProofProtocol.NonceBytes)));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    [Fact]
    public async Task Non_loopback_transport_peer_is_refused_even_with_a_loopback_host_header()
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8),
            transportPeer: IPAddress.Parse("203.0.113.25"));

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = new(
            HttpMethod.Get,
            ArcanumPresenceProofProtocol.Path);

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.PresenceNonce,
            ArcanumPresenceProofProtocol.Encode(
                RandomNumberGenerator.GetBytes(
                    ArcanumPresenceProofProtocol.NonceBytes)));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    [Fact]
    public async Task Loopback_proxy_cannot_relay_a_remote_effective_peer()
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8),
            effectiveTransportPeer: IPAddress.Parse("203.0.113.25"));

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = CreateChallengeRequest();

        request.Headers.Host = "localhost:5001";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceAuthority));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    [Fact]
    public async Task Caller_controlled_host_is_ignored_in_favor_of_the_actual_listener()
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8));

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = CreateChallengeRequest();

        request.Headers.Host = "localhost:7331";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://localhost:5001",
            Assert.Single(
                response.Headers.GetValues(
                    ArcanumApiHeaders.PresenceAuthority)));
    }

    [Fact]
    public async Task Actual_tls_listener_is_signed_as_https_even_when_request_metadata_says_http()
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8),
            includeTlsFeature: true);

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = CreateChallengeRequest();

        request.Headers.Host = "localhost:7331";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "https://localhost:5001",
            Assert.Single(
                response.Headers.GetValues(
                    ArcanumApiHeaders.PresenceAuthority)));
    }

    [Fact]
    public async Task Proof_from_one_listener_cannot_be_relayed_as_a_different_local_port()
    {
        byte[] keyDigest = SHA256.HashData("presence-test-key"u8);

        await using WebApplication app = await CreateHostAsync(
            keyDigest,
            localPort: 5002);

        HttpClient client = app.GetTestClient();

        byte[] nonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        using HttpRequestMessage request = CreateChallengeRequest(nonce);

        request.Headers.Host = "localhost:5001";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        string authority = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceAuthority));

        string encodedProof = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceProof));

        string encodedCapability = Assert.Single(
            response.Headers.GetValues(ArcanumApiHeaders.PresenceCapability));

        Assert.Equal("http://localhost:5002", authority);

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                encodedProof,
                ArcanumPresenceProofProtocol.ProofBytes,
                out byte[]? proof));

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                encodedCapability,
                ArcanumPresenceProofProtocol.CapabilityEnvelopeBytes,
                out byte[]? capabilityEnvelope));

        Assert.False(
            ArcanumPresenceProofProtocol.VerifyProof(
                keyDigest,
                nonce,
                "http://localhost:5001",
                capabilityEnvelope!,
                proof!));

        Assert.False(
            ArcanumPresenceProofProtocol.TryDecryptCapabilityEnvelope(
                keyDigest,
                nonce,
                "http://localhost:5001",
                capabilityEnvelope,
                out byte[]? relayedCapability));

        Assert.Null(relayedCapability);
    }

    [Theory]
    [InlineData(false, 5001)]
    [InlineData(true, 0)]
    public async Task Missing_or_invalid_local_listener_fails_closed_without_a_proof(
        bool includeLocalEndPoint,
        int localPort)
    {
        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8),
            localPort: localPort,
            includeLocalEndPoint: includeLocalEndPoint);

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = CreateChallengeRequest();

        request.Headers.Host = "localhost:5001";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceAuthority));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    [Fact]
    public async Task Connection_socket_feature_precedes_fallback_endpoints_for_peer_and_authority()
    {
        using ConnectedSocketPair sockets = await ConnectedSocketPair.CreateAsync();

        int socketPort = Assert.IsType<IPEndPoint>(
            sockets.Server.LocalEndPoint).Port;

        int fallbackPort = socketPort == 5001 ? 5002 : 5001;

        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8),
            transportPeer: IPAddress.Parse("203.0.113.25"),
            effectiveTransportPeer: IPAddress.Loopback,
            localPort: fallbackPort,
            transportSocket: sockets.Server);

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = CreateChallengeRequest();

        request.Headers.Host = "localhost:7331";

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            $"http://localhost:{socketPort}",
            Assert.Single(
                response.Headers.GetValues(
                    ArcanumApiHeaders.PresenceAuthority)));
    }

    [Fact]
    public async Task Disposed_connection_socket_fails_closed_without_a_proof()
    {
        using ConnectedSocketPair sockets = await ConnectedSocketPair.CreateAsync();

        sockets.Server.Dispose();

        await using WebApplication app = await CreateHostAsync(
            SHA256.HashData("presence-test-key"u8),
            transportSocket: sockets.Server);

        HttpClient client = app.GetTestClient();

        using HttpRequestMessage request = CreateChallengeRequest();

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceAuthority));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceProof));
        Assert.False(response.Headers.Contains(ArcanumApiHeaders.PresenceCapability));
    }

    private static HttpRequestMessage CreateChallengeRequest(byte[]? nonce = null)
    {
        HttpRequestMessage request = new(
            HttpMethod.Get,
            ArcanumPresenceProofProtocol.Path);

        _ = request.Headers.TryAddWithoutValidation(
            ArcanumApiHeaders.PresenceNonce,
            ArcanumPresenceProofProtocol.Encode(
                nonce
                    ?? RandomNumberGenerator.GetBytes(
                        ArcanumPresenceProofProtocol.NonceBytes)));

        return request;
    }

    private static async Task<WebApplication> CreateHostAsync(
        byte[]? keyDigest,
        IPAddress? transportPeer = null,
        IPAddress? effectiveTransportPeer = null,
        int localPort = 5001,
        bool includeLocalEndPoint = true,
        bool includeTlsFeature = false,
        Socket? transportSocket = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        ApiKeyDigestCache cache = new();

        if (keyDigest is not null)
        {
            cache.StoreDigest(keyDigest, ttlSeconds: 1);
        }

        builder.Services.AddSingleton<IApiKeyDigestCache>(cache);
        builder.Services.AddSingleton<ArcanumProcessCapabilityService>();

        WebApplication app = builder.Build();

        app.Use((context, next) =>
        {
            IPAddress peerAddress = transportPeer ?? IPAddress.Loopback;

            context.Connection.RemoteIpAddress =
                effectiveTransportPeer ?? peerAddress;

            context.Features.Set<IConnectionEndPointFeature>(
                new TestConnectionEndPointFeature(
                    new IPEndPoint(
                        peerAddress,
                        43123),
                    includeLocalEndPoint
                        ? new IPEndPoint(IPAddress.Loopback, localPort)
                        : null));

            if (transportSocket is not null)
            {
                context.Features.Set<IConnectionSocketFeature>(
                    new TestConnectionSocketFeature(transportSocket));
            }

            if (includeTlsFeature)
            {
                context.Features.Set<ITlsConnectionFeature>(
                    new TlsConnectionFeature());
            }

            return next(context);
        });

        app.MapArcanumPresenceEndpoint();

        await app.StartAsync();

        return app;
    }

    private sealed class TestConnectionEndPointFeature(
        EndPoint remoteEndPoint,
        EndPoint? localEndPoint) :
        IConnectionEndPointFeature
    {
        public EndPoint? LocalEndPoint { get; set; } = localEndPoint;

        public EndPoint? RemoteEndPoint { get; set; } = remoteEndPoint;
    }

    private sealed class TestConnectionSocketFeature(Socket socket) :
        IConnectionSocketFeature
    {
        public Socket Socket { get; } = socket;
    }

    private sealed class ConnectedSocketPair : IDisposable
    {
        private ConnectedSocketPair(
            Socket listener,
            Socket client,
            Socket server)
        {
            Listener = listener;
            Client = client;
            Server = server;
        }

        private Socket Listener { get; }

        private Socket Client { get; }

        internal Socket Server { get; }

        internal static async Task<ConnectedSocketPair> CreateAsync()
        {
            Socket listener = new(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);

            Socket client = new(
                AddressFamily.InterNetwork,
                SocketType.Stream,
                ProtocolType.Tcp);

            Socket? server = null;

            try
            {
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(backlog: 1);

                Task<Socket> accept = listener.AcceptAsync();

                await client.ConnectAsync(listener.LocalEndPoint!);

                server = await accept;

                return new ConnectedSocketPair(listener, client, server);
            }
            catch
            {
                server?.Dispose();
                client.Dispose();
                listener.Dispose();

                throw;
            }
        }

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
            Listener.Dispose();
        }
    }
}
