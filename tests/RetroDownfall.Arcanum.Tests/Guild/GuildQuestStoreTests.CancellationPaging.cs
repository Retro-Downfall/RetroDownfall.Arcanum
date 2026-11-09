using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Cancellation_work_resumes_beyond_one_page_and_deep_lineage_without_releasing_budget()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestStored parent = root;

        List<Guid> descendants = [];

        for (int index = 0; index < 105; index++)
        {
            parent = await f.ChildAsync(parent, await f.DriverAsync(parent));

            descendants.Add(parent.Quest.Id);
        }

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), root.Quest.Revision, GuildCancellationReason.OperatorRequested);

        var accepted = await f.Store.RequestCancellationAsync(f.Caller,
            new(cancel.IdempotencyKey, GuildQuestIntent.Compute(root.Quest.Id, cancel).Value), root.Quest.Id, cancel, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        var first = await f.Store.ReadCancellationWorkPageAsync(f.Caller, root.Quest.Id, 0, default);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(100, first.Value.Length);

        Assert.Equal(descendants.Take(100), first.Value.Select(item => item.QuestId));

        foreach (GuildCancellationWork item in first.Value)
        {
            GuildCancelQuestRequest request = new(Guid.NewGuid(), item.Revision, GuildCancellationReason.OperatorRequested);

            var stopped = await f.Store.RequestCancellationAsync(f.Caller,
                new(request.IdempotencyKey, GuildQuestIntent.Compute(item.QuestId, request).Value), item.QuestId, request, default);

            Assert.True(stopped.IsSuccess, stopped.Error.Message);
        }

        f.Restart();

        var resumed = await f.Store.ReadCancellationWorkPageAsync(f.Caller, root.Quest.Id, 0, default);

        Assert.True(resumed.IsSuccess, resumed.Error.Message);

        Assert.Equal(descendants.Skip(100), resumed.Value.Select(item => item.QuestId));

        var next = await f.Store.ReadCancellationWorkPageAsync(f.Caller, root.Quest.Id, first.Value[^1].IssuedSequence, default);

        Assert.True(next.IsSuccess, next.Error.Message);

        Assert.Equal(descendants.Skip(100), next.Value.Select(item => item.QuestId));

        Assert.Equal(101L, await f.CountAsync("guild_quest_cancellations"));

        Assert.Equal(0L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("fixture_settlements"));
    }
}
