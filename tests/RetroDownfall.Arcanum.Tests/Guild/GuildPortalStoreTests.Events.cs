using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildPortalStoreTests
{
    [Fact]
    public async Task Concurrent_source_events_allocate_once_and_rollback_with_source_transaction()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Guid source = Guid.NewGuid();

        Result<long>[] results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            f.Authority.InQuestTransactionAsync<long>(root.Quest.Id, (c, t, _, ct) =>
                GuildPortalEvents.AppendWithinAsync(c, t, root.Quest.Id, root.Quest.Id,
                    GuildEventKind.MessageCommitted, source, 1, null, DateTimeOffset.UtcNow, ct), default)));

        Assert.All(results, value => Assert.True(value.IsSuccess, value.Error.Message));

        Assert.Single(results.Select(value => value.Value).Distinct());

        long count = await f.CountAsync("guild_events");

        Result<long> refused = await f.Authority.InQuestTransactionAsync<long>(root.Quest.Id, async (c, t, _, ct) =>
        {
            Result<long> appended = await GuildPortalEvents.AppendWithinAsync(c, t, root.Quest.Id, root.Quest.Id,
                GuildEventKind.QuestChanged, Guid.NewGuid(), 2, null, DateTimeOffset.UtcNow, ct);

            Assert.True(appended.IsSuccess, appended.Error.Message);

            return new Error("fixture.source_failure", "Source receipt failed.");
        }, default);

        Assert.True(refused.IsFailure);

        Assert.Equal(count, await f.CountAsync("guild_events"));
    }

    [Fact]
    public async Task Issuance_persists_projection_and_source_delivery_intention_in_its_receipt_transaction()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        Assert.Equal(1L, await f.CountAsync("guild_events"));

        Assert.Equal(1L, await f.CountAsync("guild_portal_outbox"));

        Assert.True((await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).IsSuccess);

        Assert.Equal(1L, await f.CountAsync("guild_events"));

        Assert.Equal(root.Quest.Id, (await f.Store.GetAsync(f.Caller, root.Quest.Id, default)).Value.Quest.Id);
    }
}
