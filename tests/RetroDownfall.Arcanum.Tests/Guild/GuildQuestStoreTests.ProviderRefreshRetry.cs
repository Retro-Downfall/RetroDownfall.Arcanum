using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Refresh_retry_opt_out_reads_no_credentials_or_provider_and_keeps_operation_parked()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid id = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperation running = (await operations.GetAsync(id))!;

        Assert.True(await operations.TryTransitionAsync(id, running.Revision, "test-refresh-owner",
            LongRunningOperationState.ReconciliationRequired, DateTimeOffset.UtcNow, "Guild.Unavailable"));

        LongRunningOperation before = (await operations.GetAsync(id))!;

        f.Scenario.Settings.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        CountingRefreshResolver resolver = new();

        using SpendMetadataHandler handler = new();

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => handler), null!, null!, operations,
            new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        var result = await service.RetryAsync(f.Scenario.Fixture.Caller, before, default);

        Assert.True(result.IsFailure);

        Assert.Equal(0, resolver.Reads);

        Assert.Null(handler.Uri);

        LongRunningOperation after = (await operations.GetAsync(id))!;

        Assert.Equal(before.State, after.State);

        Assert.Equal(before.Revision, after.Revision);
    }

    [Fact]
    public async Task Refresh_retry_refuses_owner_close_before_writer_while_drain_is_pending()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid id = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperation running = (await operations.GetAsync(id))!;

        Assert.True(await operations.TryTransitionAsync(id, running.Revision, "test-refresh-owner",
            LongRunningOperationState.ReconciliationRequired, DateTimeOffset.UtcNow, "Guild.Unavailable"));

        LongRunningOperation before = (await operations.GetAsync(id))!;

        GuildSpendRefreshTracker tracker = new();

        Assert.True(tracker.TryEnter(f.RootId, out var entry));

        Assert.NotNull(entry);

        Task close = tracker.CloseRootAsync(f.RootId, CancellationToken.None);

        try
        {
            var reset = await f.Scenario.Fixture.Store.RetryProviderSpendRefreshAsync(f.Scenario.Fixture.Caller,
                f.QuestId, f.Scenario.EffectId, f.RefreshId, before, f.Currentness, new LongRunningOperationOwnership(), entry, default);

            Assert.True(reset.IsFailure);

            Assert.Equal(before.Revision, (await operations.GetAsync(id))!.Revision);
        }
        finally
        {
            entry.Dispose();

            await close;
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("missingBill")]
    [InlineData("missingReservation")]
    [InlineData("aliasReservation")]
    [InlineData("aliasedOperation")]
    [InlineData("liveClaim")]
    [InlineData("configuration")]
    [InlineData("authority")]
    [InlineData("stateChanged")]
    public async Task Refresh_retry_revalidates_actual_bill_and_reservation_before_durable_reset(string mutation)
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = "SELECT OperationId FROM guild_spend_refreshes WHERE RefreshId=$refresh";

        Guid id = Guid.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync()));

        await using var connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        LongRunningOperation running = (await operations.GetAsync(id))!;

        Assert.True(await operations.TryTransitionAsync(id, running.Revision, "test-refresh-owner",
            LongRunningOperationState.ReconciliationRequired, DateTimeOffset.UtcNow, "Guild.Unavailable"));

        LongRunningOperation before = (await operations.GetAsync(id))!;

        LongRunningOperationOwnership ownership = new();

        Guid claim = Guid.Empty;

        if (mutation == "liveClaim") Assert.True(ownership.TryClaim(id, out claim));

        if (mutation == "configuration") f.Scenario.Settings.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        if (mutation is not ("none" or "liveClaim" or "configuration"))
        {
            f.Query.CommandText = mutation switch
            {
                "missingBill" => "DELETE FROM BillableOperations",
                "authority" => "DELETE FROM guild_spend_authority_state",
                "stateChanged" => "UPDATE LongRunningOperations SET State=5 WHERE Id=$operation",
                "missingReservation" => "DELETE FROM BudgetReservations",
                "aliasReservation" => "UPDATE BudgetReservations SET Id=substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21)",
                "aliasedOperation" => """
                    INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,CheckpointVersion,CheckpointPayload,PublicSummary,Revision)
                    SELECT substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21),
                        Kind,State,RecoveryPolicy,CreatedAt,CheckpointVersion,CheckpointPayload,PublicSummary,Revision
                    FROM LongRunningOperations WHERE Id=$operation
                    """,
                _ => throw new InvalidOperationException("Unknown retry fixture mutation."),
            };

            Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());
        }

        Assert.True(f.Tracker.TryEnter(f.RootId, out var entry));

        Assert.NotNull(entry);

        using (entry)
        {
            var reset = await f.Scenario.Fixture.Store.RetryProviderSpendRefreshAsync(f.Scenario.Fixture.Caller,
                f.QuestId, f.Scenario.EffectId, f.RefreshId, before, f.Currentness, ownership, entry, default);

            if (claim != Guid.Empty) Assert.True(ownership.Release(id, claim));

            LongRunningOperation after = (await operations.GetAsync(id))!;

            if (mutation != "none")
            {
                Assert.True(reset.IsFailure);

                Assert.Equal(mutation == "stateChanged" ? LongRunningOperationState.Failed : LongRunningOperationState.ReconciliationRequired, after.State);

                Assert.Equal(before.Revision, after.Revision);
            }
            else
            {
                Assert.True(reset.IsSuccess, reset.IsFailure ? reset.Error.Message : null);

                Assert.Equal(LongRunningOperationState.Pending, after.State);

                Assert.Equal(before.Revision + 1, after.Revision);
            }
        }
    }
}
