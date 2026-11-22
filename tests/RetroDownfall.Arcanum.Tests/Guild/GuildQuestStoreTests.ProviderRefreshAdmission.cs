using System.Net;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Operator_correction_refuses_duplicate_real_reservation_alias_without_changes()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync(enableDailyBudget: true);

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        long revision = Assert.IsType<long>(await f.Query.ExecuteScalarAsync());

        long receipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

        long evidence = await f.Scenario.Fixture.CountAsync("guild_budget_evidence");

        long events = await f.Scenario.Fixture.CountAsync("guild_events");

        f.Query.CommandText = """
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            SELECT substr(Id,1,8)||'-'||substr(Id,9,4)||'-'||substr(Id,13,4)||'-'||substr(Id,17,4)||'-'||substr(Id,21),
                RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd FROM BudgetReservations
            """;

        Assert.Equal(1, await f.Query.ExecuteNonQueryAsync());

        GuildSpendCorrectionRequest request = new(Guid.NewGuid(), revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('B', 64));

        var result = await f.Scenario.Fixture.Store.CorrectOperatorSpendAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, request, default);

        Assert.True(result.IsFailure);

        Assert.Equal(receipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(evidence, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

        Assert.Equal(events, await f.Scenario.Fixture.CountAsync("guild_events"));

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        Assert.Equal(revision, await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT ActualCostUsd FROM BillableOperations";

        Assert.Equal("0", await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT COUNT(*) FROM BudgetReservations WHERE ReconciledUsd<>'0'";

        Assert.Equal(0L, await f.Query.ExecuteScalarAsync());
    }
}
