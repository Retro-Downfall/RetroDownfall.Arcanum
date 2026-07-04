using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Api.ProvingGrounds;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class DiWiringSmokeTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public DiWiringSmokeTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public void Host_ResolvesKeyRegisteredServices()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IServiceScope scope = _factory.Services.CreateScope();

        IServiceProvider services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<IGrimoireRepository>());

        Assert.NotNull(services.GetRequiredService<ICampaignRepository>());

        Assert.NotNull(services.GetRequiredService<IPromptRepository>());

        Assert.IsType<FakeIntelligenceProvider>(services.GetRequiredService<IArcanumIntelligenceProvider>());

        Assert.NotNull(services.GetRequiredService<IWard>());

        Assert.NotNull(services.GetRequiredService<ISanctumGuard>());

        Assert.NotNull(services.GetRequiredService<IChatClientFactory>());

        Assert.NotNull(services.GetRequiredService<ApiKeyEndpointFilter>());

        Assert.NotNull(services.GetRequiredService<IProvingGroundsArbiter>());

        Assert.NotNull(services.GetRequiredService<ProvingGroundsRunner>());

        Assert.NotNull(services.GetRequiredService<IMcpConnectionManager>());

        Assert.NotNull(services.GetRequiredService<ISanctumBreachRepository>());

    }

}
