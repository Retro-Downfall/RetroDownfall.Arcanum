using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task First_attempt_cannot_settle_cancellation_after_its_lease_expires(bool settlementAvailable)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore store = new(db, f.Scenario.Fixture.Connections);

        BlockingRefreshOperationStore operations = settlementAvailable ? new ForwardingRefreshOperationStore(store) : new BlockingRefreshOperationStore(store);

        operations.PauseSettlement = settlementAvailable;

        operations.ReleaseHeartbeat.TrySetResult();

        var locked = GuildWorkspaceLeaseHostLock.TryAcquire(Path.Combine(Path.GetTempPath(), "arcanum-refresh-settlement-" + Guid.NewGuid().ToString("N")));

        Assert.True(locked.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = locked.Value;

        BlockingRefreshStream body = new();

        SettlementManualClock clock = new(DateTimeOffset.UtcNow);

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new BlockingRefreshHandler(body)),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), clock,
            new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        Task<Result<GuildSpendRefreshResult>> attempt = service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, refresh, default);

        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await using SqliteCommand query = connection.CreateCommand();

            query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

            query.Parameters.AddWithValue("$refresh", refresh.ToString("D").ToUpperInvariant());

            Guid id = Guid.ParseExact(Assert.IsType<string>(await query.ExecuteScalarAsync()), "N");

            LongRunningOperation current = (await store.GetAsync(id))!;

            Assert.True(await store.RequestCancellationAsync(id, current.Revision, clock.GetUtcNow()));

            body.ReleaseRead.TrySetResult();

            await body.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(15));

            body.ReleaseDispose.TrySetResult();

            if (settlementAvailable)
            {
                await operations.SettlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));

                clock.Advance(TimeSpan.FromMinutes(3));

                operations.ReleaseSettlement.TrySetResult();
            }

            var result = await attempt;

            Assert.True(result.IsSuccess);

            Assert.True(result.Value.InFlight);

            Assert.Equal(id, result.Value.OperationId);

            Assert.Equal(LongRunningOperationState.Cancelling, (await store.GetAsync(id))!.State);
        }
        finally
        {
            body.ReleaseRead.TrySetResult();

            body.ReleaseDispose.TrySetResult();

            operations.ReleaseSettlement.TrySetResult();

            await attempt;
        }
    }

    private sealed class SettlementManualClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
