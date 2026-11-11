using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Cohort_stop_frontier_preserves_completed_primary_and_drains_its_active_child()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored root = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(root);

        GuildQuestStored child = await f.ChildAsync(root, driver);

        var completed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("cohort-primary")),
            root.Quest.Revision, ReceiptResult(root.Quest.Id) with { NoDispatchProven = true }, default);

        Assert.True(completed.IsSuccess, completed.Error.Message);

        var stop = await f.Authority.InQuestTransactionAsync<bool>(root.Quest.RootQuestId,
            async (connection, transaction, _, ct) =>
            {
                Result result = await f.Store.RequestCohortCancellationWithinAsync(connection, transaction, root.Quest.Id, ct);

                return result.IsSuccess ? Result<bool>.Success(true) : Result<bool>.Failure(result.Error);
            }, default);

        Assert.True(stop.IsSuccess, stop.Error.Message);

        var observed = await f.Store.GetAsync(f.Caller, root.Quest.Id, default);

        Assert.True(observed.IsSuccess, observed.Error.Message);

        Assert.Equal(QuestState.Completed, observed.Value.Quest.State);

        Assert.Equal(completed.Value.ResultId, observed.Value.Quest.ResultReceiptId);

        Assert.True(observed.Value.Quest.CancellationRequested);

        Assert.False(observed.Value.Quest.CancellationConverged);

        var pending = await f.Store.ReadCancellationWorkPageAsync(f.Caller, root.Quest.Id, 0, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        Assert.Equal(child.Quest.Id, Assert.Single(pending.Value).QuestId);
    }
}
