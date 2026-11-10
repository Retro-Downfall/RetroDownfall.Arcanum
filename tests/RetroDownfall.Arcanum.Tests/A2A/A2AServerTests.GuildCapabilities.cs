using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AServerTests
{
    [Fact]
    public void Guild_card_keeps_the_operator_configured_description()
    {
        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        settings.Integrations.A2A.AgentCardDescription = "Operator-defined research service.";

        DefaultHttpContext context = new();

        context.Request.Scheme = "https";

        context.Request.Host = new HostString("arcanum.example.test");

        var card = A2AServerEndpoints.BuildAgentCard(settings, context, "/api/conclave/a2a");

        Assert.Equal(settings.Integrations.A2A.AgentCardDescription, card.Description);
    }

    [Fact]
    public void Guild_card_advertises_peer_granted_execution_and_supported_streaming()
    {
        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        settings.Integrations.A2A.PushNotifications = true;

        DefaultHttpContext context = new();

        context.Request.Scheme = "https";

        context.Request.Host = new HostString("arcanum.example.test");

        var card = A2AServerEndpoints.BuildAgentCard(settings, context, "/api/conclave/a2a");

        Assert.NotEmpty(card.Skills!);

        Assert.True(card.Capabilities!.Streaming);

        Assert.True(card.Capabilities.PushNotifications);

        Assert.Contains("peer grant", card.Description, StringComparison.OrdinalIgnoreCase);
    }
}
