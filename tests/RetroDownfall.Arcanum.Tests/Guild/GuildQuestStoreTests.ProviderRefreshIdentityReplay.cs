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
    public async Task Admitted_refresh_replay_preserves_identity_when_operation_lookup_is_unavailable(bool throws)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string path = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(path);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        BlockingRefreshOperationStore lookup = new ForwardingRefreshOperationStore(operations);

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new SpendMetadataHandler { Status = HttpStatusCode.ServiceUnavailable }),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, lookup, new LongRunningOperationOwnership(), TimeProvider.System,
            new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        var admitted = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(admitted.IsSuccess);

        Assert.True(admitted.Value.RequiresAttention);

        lookup.ReturnMissing = !throws;

        lookup.ThrowOnGet = throws;

        var replay = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.Equal(admitted.Value.OperationId, replay.Value.OperationId);

        Assert.True(replay.Value.RequiresAttention);
    }

    [Fact]
    public async Task Lease_cancellation_with_unavailable_lookup_preserves_admitted_identity()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore inner = new(db, f.Scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = new(inner) { CancelAcquire = true, ThrowOnGet = true };

        string path = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(path);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new SpendMetadataHandler()),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System,
            new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        var result = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.NotEqual(Guid.Empty, result.Value.OperationId);

        Assert.True(result.Value.RequiresAttention);

        var saved = await f.Scenario.Fixture.Store.PrepareProviderSpendRefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, refresh, default);

        Assert.True(saved.IsSuccess);

        Assert.Equal(saved.Value.OperationId, result.Value.OperationId);
    }
}
