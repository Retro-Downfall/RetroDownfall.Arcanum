using System.Net;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_admitted_attempt_retains_identity_if_park_lookup_is_missing(bool unleased)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore inner = new(db, f.Scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = new(inner) { ReturnMissing = true };

        string path = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(path);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new SpendMetadataHandler { Status = HttpStatusCode.ServiceUnavailable }),
            new RefreshTestReadiness(hostLock) { Available = !unleased }, f.Scenario.Fixture.Gate, operations,
            new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.True(result.Value.RequiresAttention);

        var saved = await f.Scenario.Fixture.Store.PrepareProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, refresh, default);

        Assert.True(saved.IsSuccess);

        Assert.Equal(saved.Value.OperationId, result.Value.OperationId);
    }
}
