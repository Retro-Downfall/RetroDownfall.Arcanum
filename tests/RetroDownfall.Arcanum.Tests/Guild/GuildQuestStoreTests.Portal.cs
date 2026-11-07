using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Terminal_receipt_retains_result_and_settlement_delivery_intentions_without_a_subscriber()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        GuildQuestResult result = ReceiptResult(row.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true };

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("complete")), row.Quest.Revision, result, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        Assert.Equal(3L, await f.CountAsync("guild_portal_outbox"));

        Assert.Equal(3L, await f.CountAsync("guild_events"));

        f.Restart();

        Assert.Equal(3L, await f.CountAsync("guild_portal_outbox"));

        Assert.Equal(0L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(GuildDeliveryState.Pending, (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.DeliveryState);
    }

    [Fact]
    public async Task A_projection_write_failure_rolls_back_the_issuing_source_receipt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("CREATE TRIGGER fixture_projection_failure BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'event failure'); END");

        Assert.True((await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).IsFailure);

        Assert.Equal(0L, await f.CountAsync("guild_quests"));

        Assert.Equal(0L, await f.CountAsync("guild_signets"));

        Assert.Equal(0L, await f.CountAsync("guild_portal_outbox"));
    }
}
