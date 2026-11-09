using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_receipt_counts_physical_obligations_without_counting_unknown_spend_as_running(bool physicalLease)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var row = await ReceiptRootAsync(f);

        var driver = await f.DriverAsync(row);

        GuildCancelQuestRequest request = new(Guid.NewGuid(), row.Quest.Revision, GuildCancellationReason.OperatorRequested);

        GuildMutationIdentity mutation = new(request.IdempotencyKey, GuildQuestIntent.Compute(row.Quest.Id, request).Value);

        var accepted = await f.Store.RequestCancellationAsync(f.Caller, mutation, row.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        driver = driver with { OperationRevision = (await ReadFrontierOperationAsync(f, row)).Revision };

        GuildQuestResult stopped = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Canceled, LongRunningOperationRecoveryOutcomes.Cancelled), null, NoDispatchProven: true, WorkStoppedProven: true);

        var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("stopped")),
            accepted.Value.Revision, stopped, default);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        await f.ExecuteAsync("UPDATE guild_quests SET AccountingStateCode=2,Revision=Revision+1");

        if (physicalLease) await SeedCancellationLeaseAsync(f, row, GuildWorkspaceLeaseState.Held);

        f.Restart();

        var replay = await f.Store.RequestCancellationAsync(f.Caller, mutation, row.Quest.Id, request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(physicalLease ? 1 : 0, replay.Value.PendingExecutionCount);

        Assert.Equal(physicalLease ? 1 : 0, replay.Value.UnresolvedExternalEffectCount);

        Assert.Equal(!physicalLease, replay.Value.CancellationConverged);

        Assert.Equal(GuildAccountingState.Uncertain, replay.Value.AccountingState);
    }
}
