using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Failed_frontier_persists_convergence_without_rewriting_outcome_or_unknown_accounting()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var parent = await ReceiptRootAsync(f);

        var driver = await f.DriverAsync(parent);

        var child = await f.ChildAsync(parent, driver);

        GuildQuestResult failed = new(new(parent.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Failed, ErrorCodes.Guild.Unavailable), null, NoDispatchProven: true, WorkStoppedProven: true);

        Assert.True((await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("failure")),
            parent.Quest.Revision, failed, default)).IsSuccess);

        var pending = await f.Store.RefreshCancellationConvergenceAsync(f.Caller, parent.Quest.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.False(pending.Value);

        Assert.True((await f.Store.CommitResultAsync(await f.DriverAsync(child), new(Guid.NewGuid(), ReceiptDigest("stop")),
            child.Quest.Revision, ReceiptResult(child.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default)).IsSuccess);

        await f.ExecuteAsync("UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1");

        var stopped = await f.Store.RefreshCancellationConvergenceAsync(f.Caller, parent.Quest.Id, default);

        Assert.True(stopped.IsSuccess, stopped.Error.Message);

        Assert.True(stopped.Value);

        long revision = (await f.Store.GetAsync(f.Caller, parent.Quest.Id, default)).Value.Quest.Revision;

        f.Restart();

        Assert.Equal(1L, await ReceiptScalarAsync(f, $"SELECT CancellationConverged FROM guild_quests WHERE Id='{parent.Quest.Id:D}' COLLATE NOCASE"));

        Assert.True((await f.Store.RefreshCancellationConvergenceAsync(f.Caller, parent.Quest.Id, default)).Value);

        var persisted = (await f.Store.GetAsync(f.Caller, parent.Quest.Id, default)).Value.Quest;

        Assert.Equal(QuestState.Failed, persisted.State);

        Assert.Equal(failed.Receipt.ResultId, persisted.ResultReceiptId);

        Assert.Equal(GuildAccountingState.Uncertain, persisted.AccountingState);

        Assert.Equal(revision, persisted.Revision);

        Assert.Equal(2L, await f.CountAsync("fixture_settlements"));
    }
}
