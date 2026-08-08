using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Pins the behaviour of the untrusted egress connect callback: every resolved address is
/// re-validated before a socket is opened, an elapsed connect bound fails closed as a transport
/// error rather than a silent success, and a cancellation the guard did not raise is never
/// relabelled as a connect timeout.
/// </summary>
[Collection("OutboundUrlGuardDns")]
public sealed class OutboundUrlGuardEgressConnectTests : IDisposable
{

    /// <summary>
    /// TEST-NET-3 (RFC 5737). Combined with port 0 below, <c>connect</c> is rejected by the OS
    /// immediately and no packet leaves the host, so these tests never depend on the network.
    /// </summary>
    private static readonly IPAddress UnconnectableAddress = IPAddress.Parse("203.0.113.1");

    private static readonly IPAddress SecondUnconnectableAddress = IPAddress.Parse("198.51.100.1");

    private const string EgressUrl = "http://egress-guard.example:0/hook";

    private const string EgressHost = "egress-guard.example";

    private readonly IDnsResolver _originalResolver;

    public OutboundUrlGuardEgressConnectTests()
    {

        _originalResolver = OutboundUrlGuard.DnsResolver;

    }

    public void Dispose()
    {

        OutboundUrlGuard.DnsResolver = _originalResolver;

        OutboundUrlGuard.ResetTestSeams();

    }

    [Fact]
    public async Task EveryPinnedAddressRefusingConnection_FailsAsTransportErrorCarryingEveryCause()
    {

        FakeDnsResolver fake = new();

        fake.Add(EgressHost, UnconnectableAddress, SecondUnconnectableAddress);

        OutboundUrlGuard.DnsResolver = fake;

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler();

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains($"Could not connect to '{EgressHost}' on port 0.", Flatten(failure), StringComparison.Ordinal);

        AggregateException causes = Assert.IsType<AggregateException>(FindInner<AggregateException>(failure));

        Assert.Equal(2, causes.InnerExceptions.Count);

        Assert.All(causes.InnerExceptions, static cause => Assert.IsAssignableFrom<SocketException>(cause));

    }

    [Fact]
    public async Task HostResolvingToLoopbackAtConnectTime_RefusesToOpenTheSocket()
    {

        FakeDnsResolver fake = new();

        fake.Add(EgressHost, IPAddress.Loopback);

        OutboundUrlGuard.DnsResolver = fake;

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler();

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains(
            "Outbound URL targets a loopback, private, or link-local address and is not permitted.",
            Flatten(failure),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task HostWithNoDnsAnswerAtConnectTime_RefusesToOpenTheSocket()
    {

        OutboundUrlGuard.DnsResolver = new FakeDnsResolver();

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler();

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains(
            $"Could not resolve host '{EgressHost}'.",
            Flatten(failure),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task ConnectBoundElapsingDuringResolution_FailsClosedAsTransportTimeout()
    {

        OutboundUrlGuard.DnsResolver = new HangingDnsResolver();

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(
            TimeSpan.FromMilliseconds(50));

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains(
            $"Timed out resolving '{EgressHost}' within the outbound connect timeout.",
            Flatten(failure),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task ConnectBoundElapsingBeforeTheFirstConnect_FailsClosedAsTransportError()
    {

        // Resolution deliberately outlives the connect bound while ignoring the token, so the bound is
        // already spent when the first socket is attempted.
        OutboundUrlGuard.DnsResolver = new SlowUncancellableDnsResolver(
            TimeSpan.FromMilliseconds(400),
            [UnconnectableAddress]);

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(
            TimeSpan.FromMilliseconds(25));

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains($"Could not connect to '{EgressHost}' on port 0.", Flatten(failure), StringComparison.Ordinal);

        AggregateException causes = Assert.IsType<AggregateException>(FindInner<AggregateException>(failure));

        Assert.All(causes.InnerExceptions, static cause => Assert.IsAssignableFrom<OperationCanceledException>(cause));

    }

    [Fact]
    public async Task ConnectBoundElapsingAfterEarlierRefusals_ReportsBothRefusalsAndTheElapsedBound()
    {

        IPAddress[] many = new IPAddress[20_000];

        Array.Fill(many, UnconnectableAddress);

        FakeDnsResolver fake = new();

        fake.Add(EgressHost, many);

        OutboundUrlGuard.DnsResolver = fake;

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(
            TimeSpan.FromMilliseconds(150));

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        AggregateException causes = Assert.IsType<AggregateException>(FindInner<AggregateException>(failure));

        Assert.Contains(causes.InnerExceptions, static cause => cause is SocketException);

        Assert.Contains(causes.InnerExceptions, static cause => cause is OperationCanceledException);

        // The bound stops the sweep; it must not walk all 20,000 pinned candidates.
        Assert.True(causes.InnerExceptions.Count < many.Length);

    }

    [Fact]
    public async Task ResolverCancellationWhileTheConnectBoundIsIntact_IsNotRewrittenAsAConnectTimeout()
    {

        OutboundUrlGuard.DnsResolver = new SpontaneouslyCancellingDnsResolver();

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(
            TimeSpan.FromMinutes(5));

        using HttpClient client = new(handler, disposeHandler: false);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(EgressUrl));

        Assert.DoesNotContain("outbound connect timeout", Flatten(failure), StringComparison.Ordinal);

    }

    [Fact]
    public async Task PinnedAddressListPoisonedAfterValidation_RefusesToOpenTheSocket()
    {

        FakeDnsResolver fake = new();

        fake.Add(EgressHost, UnconnectableAddress);

        OutboundUrlGuard.DnsResolver = fake;

        // Simulates a rebind landing between validation and connect: the callback re-checks every
        // pinned address, so a loopback target substituted afterwards must still be refused.
        OutboundUrlGuard.SetPinnedAddressRewriterForTests(static _ => [IPAddress.Loopback]);

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler();

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains(
            "Outbound URL targets a loopback, private, or link-local address and is not permitted.",
            Flatten(failure),
            StringComparison.Ordinal);

    }

    [Fact]
    public async Task PinnedAddressListEmptiedAfterValidation_RefusesToOpenTheSocket()
    {

        FakeDnsResolver fake = new();

        fake.Add(EgressHost, UnconnectableAddress);

        OutboundUrlGuard.DnsResolver = fake;

        // With no pinned candidate left there is nothing validated to connect to; the callback must
        // fail rather than fall through to an unpinned connection.
        OutboundUrlGuard.SetPinnedAddressRewriterForTests(static _ => []);

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler();

        using HttpClient client = new(handler, disposeHandler: false);

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(EgressUrl));

        Assert.Contains($"Could not connect to '{EgressHost}' on port 0.", Flatten(failure), StringComparison.Ordinal);

        Assert.Null(FindInner<AggregateException>(failure));

    }

    private static string Flatten(Exception exception)
    {

        StringBuilder builder = new();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {

            builder.AppendLine(current.Message);

        }

        return builder.ToString();

    }

    private static Exception? FindInner<T>(Exception exception)
        where T : Exception
    {

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {

            if (current is T match)
            {
                return match;
            }

        }

        return null;

    }

    private sealed class HangingDnsResolver : IDnsResolver
    {

        public async Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

            return [];

        }

    }

    private sealed class SlowUncancellableDnsResolver(TimeSpan delay, IPAddress[] addresses) : IDnsResolver
    {

        public async Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {

            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);

            return addresses;

        }

    }

    private sealed class SpontaneouslyCancellingDnsResolver : IDnsResolver
    {

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException();

    }

}
