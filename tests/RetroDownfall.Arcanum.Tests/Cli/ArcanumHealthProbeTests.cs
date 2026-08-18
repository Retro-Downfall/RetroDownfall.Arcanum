using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

using RetroDownfall.Arcanum.Cli.Services;

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
    public async Task An_http_request_error_is_classified_without_an_inner_socket_exception(
        HttpRequestError error,
        HealthProbeState expected)
    {

        HealthProbeResult probe = await ProbeAsync(
            new HttpRequestException(error, "The request failed."));

        Assert.Equal(expected, probe.State);

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
    public async Task An_inner_socket_error_stays_authoritative_for_the_connect_phase(
        SocketError socketError,
        HealthProbeState expected)
    {

        HealthProbeResult probe = await ProbeAsync(
            new HttpRequestException(
                HttpRequestError.Unknown,
                "An error occurred while sending the request.",
                new SocketException((int)socketError)));

        Assert.Equal(expected, probe.State);

    }

    [Fact]
    public async Task A_handshake_failure_is_still_reported_as_tls()
    {

        HealthProbeResult probe = await ProbeAsync(
            new HttpRequestException(
                "The SSL connection could not be established.",
                new AuthenticationException("cert invalid")));

        Assert.Equal(HealthProbeState.TlsFailure, probe.State);

    }

    private static async Task<HealthProbeResult> ProbeAsync(Exception failure)
    {

        using HttpClient client = new(new ThrowingHandler(failure));

        return await ArcanumHealthProbe.ProbeAsync(
            client,
            new Uri("http://localhost:5001/api/health"),
            "test-key",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw failure;

    }

}
