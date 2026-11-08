using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AServerTests
{
    [Fact]
    public void Guild_card_refuses_to_advertise_unavailable_remote_execution_or_streaming()
    {
        ArcanumSettings settings = EnabledSettings();

        settings.Features.Guild = true;

        settings.Integrations.A2A.PushNotifications = true;

        DefaultHttpContext context = new();

        context.Request.Scheme = "https";

        context.Request.Host = new HostString("arcanum.example.test");

        var card = A2AServerEndpoints.BuildAgentCard(settings, context, "/api/conclave/a2a");

        Assert.Empty(card.Skills!);

        Assert.False(card.Capabilities!.Streaming);

        Assert.False(card.Capabilities.PushNotifications);

        Assert.Contains("unavailable", card.Description, StringComparison.OrdinalIgnoreCase);
    }
}
