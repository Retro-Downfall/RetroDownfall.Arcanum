using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task A_root_result_can_be_acknowledged_once_without_parent_input_or_redispatch()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult result = ReceiptResult(row.Quest.Id);

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("complete")), row.Quest.Revision, result, default)).IsSuccess);

        var acknowledged = await f.Store.AcknowledgeRootResultAsync(f.Caller, row.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default);

        Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

        Assert.Equal(GuildDeliveryState.Applied, acknowledged.Value);

        f.Restart();

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, row.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 0, default)).IsSuccess);

        Assert.Equal(2L, await ReceiptScalarAsync(f, "SELECT Revision FROM guild_result_outbox"));

        Assert.Equal(0L, await f.CountAsync("guild_result_inputs"));

        Assert.Equal(1L, await f.CountAsync("fixture_exposures"));

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, row.Quest.Id, Guid.NewGuid(),
            result.Receipt.CanonicalContentDigest, 2, default)).IsFailure);
    }

    [Fact]
    public async Task A_root_acknowledgement_fault_keeps_delivery_pending_until_a_retry_commits()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        await ConfirmReceiptDispatchAsync(f, driver);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult result = ReceiptResult(row.Quest.Id);

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("complete")), row.Quest.Revision, result, default)).IsSuccess);

        await f.ExecuteAsync("CREATE TRIGGER fixture_ack_fault BEFORE UPDATE ON guild_result_outbox BEGIN SELECT RAISE(ABORT,'fixture acknowledgement fault'); END;");

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, row.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default)).IsFailure);

        Assert.Equal(GuildDeliveryState.Pending, (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.DeliveryState);

        await f.ExecuteAsync("DROP TRIGGER fixture_ack_fault");

        Assert.True((await f.Store.AcknowledgeRootResultAsync(f.Caller, row.Quest.Id, result.Receipt.ResultId,
            result.Receipt.CanonicalContentDigest, 1, default)).IsSuccess);
    }

    [Fact]
    public async Task Successful_results_require_a_content_reference_even_when_the_body_is_empty()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        string emptyDigest = ReceiptDigest("");

        GuildQuestResult bodyless = new(new(row.Quest.Id, Guid.NewGuid(), emptyDigest, null, QuestState.Completed, null),
            null, NoDispatchProven: true);

        var rejected = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("bodyless")), row.Quest.Revision, bodyless, default);

        Assert.True(rejected.IsFailure, "Successful result shape must agree with the catalog's immutable content contract.");

        Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

        GuildQuestResult empty = bodyless with
        {
            Receipt = bodyless.Receipt with { Content = new(Guid.NewGuid(), GuildContentKind.QuestResult, 1, emptyDigest, "text/plain", 0, false) },
            Body = []
        };

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("empty")), row.Quest.Revision, empty, default)).IsSuccess);

        f.Restart();

        Assert.Equal(QuestState.Completed, (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.State);
    }
}
