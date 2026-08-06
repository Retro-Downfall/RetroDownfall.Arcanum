using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Transport policy for the Archmage Client's named outbound <see cref="HttpClient"/>, asserted through
/// the real host so the production registration cannot drift.
/// </summary>
/// <remarks>
/// <c>dispatch_sending</c> blocks until the remote agent reaches a terminal state, and a remote
/// Apprentice can legitimately work for far longer than <see cref="HttpClient"/>'s 100-second default.
/// Issue #55 replaces arbitrary whole-operation deadlines with connection/idle-I/O bounds plus caller
/// cancellation, so this client carries no total deadline and instead bounds connection establishment.
/// </remarks>
[Collection("ApiHost")]
public sealed class A2AOutboundHttpClientTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public A2AOutboundHttpClientTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public void OutboundClient_HasNoArbitraryWholeOperationDeadline()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpClient client = _factory.Services
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(A2AClientService.OutboundHttpClientName);

        // A 100-second ceiling would abort a perfectly healthy long-running remote Sending.
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);

    }

    [Fact]
    public void UntrustedEgressHandler_BoundsConnectionEstablishmentWhenAsked()
    {

        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(
            connectTimeout: TimeSpan.FromSeconds(30));

        Assert.False(handler.AllowAutoRedirect);

        Assert.False(handler.UseProxy);

    }

    [Fact]
    public async Task UntrustedEgressHandler_ConnectTimeout_FailsTheConnectionRatherThanHanging()
    {

        // A host whose DNS never resolves stands in for a black-holed connect: the connect timeout must
        // surface as a request failure instead of waiting on the OS TCP timeout.
        using SocketsHttpHandler handler = OutboundUrlGuard.CreateUntrustedEgressHandler(
            connectTimeout: TimeSpan.FromMilliseconds(1));

        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync("http://a2a-connect-timeout.invalid/"));

    }

}
