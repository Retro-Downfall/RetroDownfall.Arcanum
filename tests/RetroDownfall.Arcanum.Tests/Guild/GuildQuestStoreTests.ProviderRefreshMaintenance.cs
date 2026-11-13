using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Provider_refresh_rolls_back_if_maintenance_becomes_pending_during_final_write()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        IGrimoireClosingOwner? closing = null;

        RefreshMaintenanceConnections connections = new(f.Scenario.Fixture.Connections, () =>
        {
            var begun = f.Scenario.Fixture.Gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(71));

            Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

            closing = begun.Value;
        });

        using SignetAuthority authority = new(new(connections), f.Scenario.Fixture.Gate, f.Scenario.Fixture.Host, TimeProvider.System);

        GuildQuestStore store = new(new(connections, f.Scenario.Fixture.Gate, TimeProvider.System), authority,
            f.Scenario.Fixture.Host, new GuildBudgetLedger(), TimeProvider.System);

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        string lockPath = Path.Combine(Path.GetTempPath(), "arcanum-refresh-lock-" + Guid.NewGuid().ToString("N"));

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(lockPath);

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        GuildSpendRefreshService service = new(store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => new SpendMetadataHandler()),
            new RefreshTestReadiness(hostLock), f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(),
            TimeProvider.System, new GuildSpendRefreshTracker());

        f.Query.CommandText = "CREATE TRIGGER fixture_close_during_refresh BEFORE UPDATE ON BillableOperations BEGIN SELECT fixture_close_during_refresh(); END";

        await f.Query.ExecuteNonQueryAsync();

        long receipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

        try
        {
            _ = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, Guid.NewGuid(), default);

            Assert.NotNull(closing);
        }
        finally
        {
            if (closing is not null)
            {
                Assert.True((await f.Scenario.Fixture.Gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), default)).IsSuccess);

                await closing.DisposeAsync();
            }
        }

        Assert.Equal(receipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        f.Query.CommandText = "SELECT COUNT(*) FROM guild_spend_refreshes WHERE AcceptedReceiptId IS NOT NULL";

        Assert.Equal(0L, await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT ActualCostUsd FROM BillableOperations";

        Assert.Equal(0m, ExactUsdText.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync())));
    }

    private sealed class RefreshMaintenanceConnections(IGrimoireOrdinaryConnectionFactory inner, Action begin)
        : IGrimoireOrdinaryConnectionFactory
    {
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(SqliteConnection connection,
            CovenantSqliteConnectionMode mode, CancellationToken cancellationToken) => inner.AcquireScopedAsync(connection, mode, cancellationToken);

        public async Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken)
        {
            var acquired = await inner.OpenFreshAsync(kind, cancellationToken);

            if (acquired.IsFailure) return acquired.Error;

            acquired.Value.Connection.CreateFunction("fixture_close_during_refresh", () => { begin(); return 0; });

            return acquired;
        }
    }
}
