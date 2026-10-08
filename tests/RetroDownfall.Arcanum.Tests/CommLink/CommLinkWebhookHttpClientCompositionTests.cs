using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Tests.CommLink;

/// <summary>
/// The dispatcher's own URL check is a point-in-time DNS check. The real SSRF defence (no automatic
/// redirect following, connect-time IP pinning) lives only in the primary handler the named client is
/// composed with, and every dispatcher test injects its own <see cref="HttpClient"/>. These tests pin
/// the composition so removing the untrusted egress handler turns a test red.
/// </summary>
public sealed class CommLinkWebhookHttpClientCompositionTests
{
    [Fact]
    public void Webhook_client_uses_the_untrusted_egress_handler()
    {
        AssertUsesUntrustedEgressHandler(WebhookCommLinkDispatcher.HttpClientName);
    }

    [Fact]
    public void Browse_web_client_uses_the_untrusted_egress_handler()
    {
        AssertUsesUntrustedEgressHandler(ArcanumBrowseWebConstants.HttpClientName);
    }

    private static void AssertUsesUntrustedEgressHandler(string clientName)
    {
        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        HttpMessageHandler pipeline =
            provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        HttpMessageHandler current = pipeline;

        while (current is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            current = delegating.InnerHandler;
        }

        SocketsHttpHandler primary = Assert.IsType<SocketsHttpHandler>(current);

        // A webhook or browsed host that answers 302 to a link-local metadata address would be followed
        // without this, and a DNS rebind between validation and connect would be dialed without the
        // connect-time address pin.
        Assert.False(primary.AllowAutoRedirect);

        Assert.NotNull(primary.ConnectCallback);
    }
}
