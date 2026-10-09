using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class CampaignInvocationBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Eligible_Campaign_turns_choose_durable_Session_authority_and_private_headers_before_any_work(bool existingSession, bool emptyStatelessList)
    {
        using ServiceProvider services = Services(true);

        DefaultHttpContext http = new() { RequestServices = services };

        PingRequest request = new("continue", SessionId: existingSession ? Guid.NewGuid() : null,
            StatelessMessages: emptyStatelessList ? [] : null);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, request, Campaign());

        Assert.Equal(ArcanumExecutionSurface.SessionBackedOperatorTurn, invocation.Surface);

        Assert.True(CovenantRequestFeatures.IsProtectedResponse(http));

        CovenantProtectedResponseHeaders.ApplyStreamingDefaultWithoutWeakening(http);

        Assert.Equal("no-store, private", http.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", http.Response.Headers.Pragma.ToString());

        Assert.Equal("0", http.Response.Headers.Expires.ToString());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-context")]
    [InlineData("stateless")]
    [InlineData("global")]
    public void Ineligible_new_turns_keep_their_existing_surface_and_cache_behavior(string exclusion)
    {
        using ServiceProvider services = Services(exclusion != "disabled");

        DefaultHttpContext http = new() { RequestServices = services };

        if (exclusion == "no-context")
        {
            http.Items[ArcanumInvocationContexts.NoContextRequestFeatureKey] = true;
        }

        PingRequest request = new("continue", StatelessMessages: exclusion == "stateless" ? [new("user", "continue")] : null);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, request,
            exclusion == "global" ? null : Campaign());

        Assert.Equal(ArcanumExecutionSurface.StatelessOperatorTurn, invocation.Surface);

        Assert.False(CovenantRequestFeatures.IsProtectedResponse(http));
    }

    private static ServiceProvider Services(bool enabled)
    {
        ArcanumSettings settings = new();

        settings.Features.CampaignRollups = enabled;

        return new ServiceCollection().AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(settings)).BuildServiceProvider();
    }

    private static CanonicalCampaignContext Campaign() => CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null);
}
