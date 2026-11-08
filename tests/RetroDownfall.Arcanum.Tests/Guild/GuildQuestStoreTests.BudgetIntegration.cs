using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Real_budget_admission_and_replay_share_one_durable_account_and_no_daily_reservation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseBudgetParticipant(new GuildBudgetLedger());

        Result<GuildQuestStored> issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(1L, await f.CountAsync("guild_budget_accounts"));

        Assert.Equal(1L, await f.CountAsync("guild_quest_budget"));

        Assert.Equal(0L, await f.CountAsync("BudgetReservations"));

        f.Restart();

        Result<GuildQuestStored> replay = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(issued.Value.AllocationAccountId, replay.Value.AllocationAccountId);

        Assert.Equal(1L, await f.CountAsync("guild_budget_accounts"));
    }

    [Fact]
    public void Quest_store_exposes_authorized_tree_spend_port()
    {
        Assert.True(typeof(IGuildTreeSpendLedger).IsAssignableFrom(typeof(GuildQuestStore)));
    }

    [Fact]
    public async Task Tree_spend_reads_durable_accounts_and_refuses_a_different_installation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseBudgetParticipant(new GuildBudgetLedger());

        Result<GuildQuestStored> issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        IGuildTreeSpendLedger port = Assert.IsAssignableFrom<IGuildTreeSpendLedger>(f.Store);

        Result<GuildSpendDto> read = await port.ReadAsync(f.Caller, issued.Value.Quest.Id, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(0m, read.Value.KnownUsd);

        Assert.False(read.Value.UnknownUsd);

        Assert.Equal(GuildBudgetMode.Unlimited, read.Value.Remaining.Usd.Mode);

        GuildCallerContext stranger = new(GuildCallerKind.Operator, f.Caller.PrincipalId!.Value,
            Guid.NewGuid(), Guid.NewGuid());

        Assert.True((await port.ReadAsync(stranger, issued.Value.Quest.Id, default)).IsFailure);
    }

    [Fact]
    public void Unresolved_accounting_evidence_pins_cleanup_independently_of_quest_projection()
    {
        Assert.Contains("guild_budget_effects", GuildQuestRetentionPins.ExecutionUnresolved, StringComparison.Ordinal);

        Assert.Contains("guild_budget_accounts", GuildQuestRetentionPins.ExecutionUnresolved, StringComparison.Ordinal);
    }
}
