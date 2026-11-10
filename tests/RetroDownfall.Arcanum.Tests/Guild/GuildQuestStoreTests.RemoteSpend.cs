using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Remote_physical_spend_is_visible_once_in_existing_external_daily_authority(bool priced)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger budget = new();

        f.UseBudgetParticipant(budget);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = Guid.NewGuid();

        var prepared = await f.Store.PrepareDispatchAsync(driver, dispatchId, ReceiptDigest("external-spend"), row.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            var effect = await budget.PrepareEffectWithinAsync(connection, transaction,
                new(row.Quest.Id, row.Quest.Id, dispatchId, row.Quest.RunId, GuildBudgetEffectKind.External, row.Quest.Id, null, null, null), default);

            Assert.True(effect.IsSuccess, effect.Error.Message);

            await using SqliteCommand correlate = connection.CreateCommand();

            correlate.Transaction = transaction;

            correlate.CommandText = "INSERT INTO guild_remote_effects(EffectId,QuestId,DispatchId,SettledAtUtc) VALUES($quest,$quest,$dispatch,$now)";

            correlate.Parameters.AddWithValue("$quest", row.Quest.Id.ToString("D").ToUpperInvariant());
            correlate.Parameters.AddWithValue("$dispatch", dispatchId.ToString("D").ToUpperInvariant());
            correlate.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await correlate.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
        }

        var exposed = await f.Store.MarkPossiblyDispatchedAsync(driver, dispatchId, prepared.Value.Revision, default);

        Assert.True(exposed.IsSuccess, exposed.Error.Message);

        GuildBudgetEvidence receipt = new(Guid.NewGuid(), ReceiptDigest("remote receipt"), priced ? 1.25m : 0m,
            priced ? 40 : 0, !priced, !priced, true);

        for (int replay = 0; replay < 2; replay++)
        {
            await using SqliteTransaction transaction = connection.BeginTransaction();

            var applied = await budget.ApplyEvidenceWithinAsync(connection, transaction, row.Quest.Id, receipt, default);

            Assert.True(applied.IsSuccess, applied.Error.Message);

            await transaction.CommitAsync();
        }

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        A2AExternalSpendLedger external = new(db, TimeProvider.System, NullLogger<A2AExternalSpendLedger>.Instance);

        ExternalSpendSummary summary = await external.GetTodayAsync();

        Assert.Equal(priced ? 1.25m : 0m, summary.KnownCostUsd);

        Assert.Equal(priced ? 40 : 0, summary.KnownTokens);

        Assert.Equal(priced ? 1 : 0, summary.PricedSendings);

        Assert.Equal(priced ? 0 : 1, summary.UnpricedSendings);

        Assert.Equal(1L, await f.CountAsync("guild_budget_effects"));

        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
    }
}
