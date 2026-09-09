using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #33 — the probe's whole job is to separate "nothing is listening" (safe to auto-start a
/// host) from "something answered" (never safe). Every transport failure it can see must land on the
/// correct side of that line, so the classification table is pinned directly rather than only
/// through the spawn decision it feeds.
/// </summary>
public sealed class ArcanumHealthProbeTests
{
    [Theory]
    [InlineData(HttpRequestError.InvalidResponse, HealthProbeState.UnexpectedResponder)]
    [InlineData(HttpRequestError.ResponseEnded, HealthProbeState.UnexpectedResponder)]
    [InlineData(HttpRequestError.HttpProtocolError, HealthProbeState.UnexpectedResponder)]
    [InlineData(HttpRequestError.VersionNegotiationError, HealthProbeState.UnexpectedResponder)]
    [InlineData(HttpRequestError.ExtendedConnectNotSupported, HealthProbeState.UnexpectedResponder)]
    [InlineData(HttpRequestError.ConfigurationLimitExceeded, HealthProbeState.UnexpectedResponder)]
    [InlineData(HttpRequestError.NameResolutionError, HealthProbeState.DnsFailure)]
    [InlineData(HttpRequestError.SecureConnectionError, HealthProbeState.TlsFailure)]
    [InlineData(HttpRequestError.Unknown, HealthProbeState.NetworkUnreachable)]
    [InlineData(HttpRequestError.ConnectionError, HealthProbeState.NetworkUnreachable)]
    public void An_http_request_error_is_classified_without_an_inner_socket_exception(
        HttpRequestError error,
        HealthProbeState expected)
    {
        HealthProbeState state = ArcanumHealthProbe.ClassifyHttpRequestException(
            new HttpRequestException(error, "The request failed."));

        Assert.Equal(expected, state);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, HealthProbeState.ConnectionRefused)]
    [InlineData(SocketError.HostNotFound, HealthProbeState.DnsFailure)]
    [InlineData(SocketError.NoData, HealthProbeState.DnsFailure)]
    [InlineData(SocketError.NetworkUnreachable, HealthProbeState.NetworkUnreachable)]
    [InlineData(SocketError.HostUnreachable, HealthProbeState.NetworkUnreachable)]
    [InlineData(SocketError.AddressNotAvailable, HealthProbeState.NetworkUnreachable)]
    [InlineData(SocketError.ConnectionReset, HealthProbeState.UnexpectedResponder)]
    [InlineData(SocketError.ConnectionAborted, HealthProbeState.UnexpectedResponder)]
    [InlineData(SocketError.Shutdown, HealthProbeState.UnexpectedResponder)]
    public void An_inner_socket_error_stays_authoritative_for_the_connect_phase(
        SocketError socketError,
        HealthProbeState expected)
    {
        HealthProbeState state = ArcanumHealthProbe.ClassifyHttpRequestException(
            new HttpRequestException(
                HttpRequestError.Unknown,
                "An error occurred while sending the request.",
                new SocketException((int)socketError)));

        Assert.Equal(expected, state);
    }

    [Fact]
    public void A_handshake_failure_is_still_reported_as_tls()
    {
        HealthProbeState state = ArcanumHealthProbe.ClassifyHttpRequestException(
            new HttpRequestException(
                "The SSL connection could not be established.",
                new AuthenticationException("cert invalid")));

        Assert.Equal(HealthProbeState.TlsFailure, state);
    }

    [Fact]
    public async Task Authenticated_probe_renews_once_after_unauthorized_and_uses_a_distinct_capability()
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(requestNumber =>
            new HttpResponseMessage(
                requestNumber == 1
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        HealthProbeResult probe = await ArcanumHealthProbe.ProbeAuthenticatedAsync(
            client,
            new Uri("http://localhost:5001/api/health"),
            credentials,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HealthProbeState.Healthy, probe.State);
        Assert.Equal(2, handler.Capabilities.Count);
        Assert.NotEqual(handler.Capabilities[0], handler.Capabilities[1]);
    }

    [Fact]
    public async Task Authenticated_probe_returns_persistent_unauthorized_after_exactly_two_requests()
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        HealthProbeResult probe = await ArcanumHealthProbe.ProbeAuthenticatedAsync(
            client,
            new Uri("http://localhost:5001/api/health"),
            credentials,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HealthProbeState.Unauthorized, probe.State);
        Assert.Equal(2, handler.Capabilities.Count);
    }

    [Fact]
    public async Task Authenticated_probe_preserves_transport_failure_classification()
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        using HttpClient client = new(
            new ThrowingHandler(
                new HttpRequestException(
                    HttpRequestError.InvalidResponse,
                    "The response was malformed.")))
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        HealthProbeResult probe = await ArcanumHealthProbe.ProbeAuthenticatedAsync(
            client,
            new Uri("http://localhost:5001/api/health"),
            credentials,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HealthProbeState.UnexpectedResponder, probe.State);
    }

    [Theory]
    [InlineData(false, "No local API credential was found")]
    [InlineData(true, "could not be read")]
    public async Task Authenticated_probe_preserves_the_credential_failure_diagnosis(
        bool corrupted,
        string expectedMessage)
    {
        using ArcanumApiCredentialLease credentials = corrupted
            ? ArcanumApiCredentialLeaseTestFactory.Create(
                static _ => Task.FromResult(
                    SecretStoreReadResult.Corrupted("mirror unreadable")),
                static _ => Task.FromResult(
                    SecretStoreReadResult.Corrupted("primary unreadable")),
                "test-key")
            : ArcanumApiCredentialLeaseTestFactory.Create((string?)null);

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        HealthProbeResult probe = await ArcanumHealthProbe.ProbeAuthenticatedAsync(
            client,
            new Uri("http://localhost:5001/api/health"),
            credentials,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(HealthProbeState.Unauthorized, probe.State);
        Assert.Contains(
            expectedMessage,
            probe.Error ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Capabilities);
    }

    [Fact]
    public async Task Health_deadline_does_not_cancel_a_local_secure_storage_prompt()
    {
        TaskCompletionSource credentialReadStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<SecretStoreReadResult> releaseCredential = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create(
                static _ => Task.FromResult(SecretStoreReadResult.Missing()),
                _ =>
                {
                    credentialReadStarted.TrySetResult();

                    return releaseCredential.Task;
                },
                "test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        Task<HealthProbeResult> pending = ArcanumHealthProbe.ProbeAuthenticatedAsync(
            client,
            new Uri("http://localhost:5001/api/health"),
            credentials,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        await credentialReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.False(pending.IsCompleted);

        releaseCredential.TrySetResult(SecretStoreReadResult.Ok("test-key"));

        HealthProbeResult probe = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HealthProbeState.Healthy, probe.State);
    }

    private sealed class RecordingHandler(
        Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;

        internal List<string> Capabilities { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Capabilities.Add(
                Assert.Single(
                    request.Headers.GetValues(
                        ArcanumApiHeaders.ProcessCapability)));

            int requestNumber = Interlocked.Increment(ref _requestCount);

            return Task.FromResult(responseFactory(requestNumber));
        }
    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw failure;
    }
}
