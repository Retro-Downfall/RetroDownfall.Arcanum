using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("legacy-canceled")]
    public async Task Tree_uses_current_stopped_subtree_proof_before_the_convergence_marker_is_refreshed(string outcome)
    {
        await using Fixture f = await Fixture.CreateAsync();

        var root = await ReceiptRootAsync(f);

        var driver = await f.DriverAsync(root);

        var child = await f.ChildAsync(root, driver);

        bool canceled = outcome != "failed";

        if (!canceled)
        {
            GuildQuestResult failed = new(new(root.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
                QuestState.Failed, ErrorCodes.Guild.Unavailable), null, NoDispatchProven: true, WorkStoppedProven: true);

            var committed = await f.Store.CommitResultAsync(driver, new(Guid.NewGuid(), ReceiptDigest("failure")),
                root.Quest.Revision, failed, default);

            Assert.True(committed.IsSuccess, committed.Error.Message);
        }
        else
        {
            long revision = root.Quest.Revision;

            if (outcome == "canceled")
            {
                GuildCancelQuestRequest request = new(Guid.NewGuid(), root.Quest.Revision, GuildCancellationReason.OperatorRequested);

                var accepted = await f.Store.RequestCancellationAsync(f.Caller,
                    new(request.IdempotencyKey, GuildQuestIntent.Compute(root.Quest.Id, request).Value), root.Quest.Id, request, default);

                Assert.True(accepted.IsSuccess, accepted.Error.Message);

                revision = accepted.Value.Revision;
            }

            // A retained stopped receipt and terminal operation are authoritative even when
            // the optimization marker was never refreshed by an older host.
            string id = root.Quest.Id.ToString("D").ToUpperInvariant();

            await f.ExecuteAsync($"""
                INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                VALUES('{id}','{Guid.NewGuid().ToString("D").ToUpperInvariant()}',X'{ReceiptDigest("")}',5,'operation.cancelled',1,1,
                    '{Guid.NewGuid().ToString("D").ToUpperInvariant()}',{revision + 1},'2026-11-08T00:00:00.0000000Z');
                UPDATE LongRunningOperations SET State=4,TerminalErrorCode='operation.cancelled',LeaseOwner=NULL,LeaseExpiresAt=NULL
                    WHERE Id='{root.Quest.OperationId:N}';
                UPDATE guild_quests SET Revision=Revision+1 WHERE Id='{id}';
                """);
        }

        var stopped = await f.Store.CommitResultAsync(await f.DriverAsync(child), new(Guid.NewGuid(), ReceiptDigest("child stopped")),
            child.Quest.Revision, ReceiptResult(child.Quest.Id) with { NoDispatchProven = true, WorkStoppedProven = true }, default);

        Assert.True(stopped.IsSuccess, stopped.Error.Message);

        Assert.Equal(0L, await ReceiptScalarAsync(f,
            $"SELECT CancellationConverged FROM guild_quests WHERE Id='{root.Quest.Id:D}' COLLATE NOCASE"));

        var current = await f.Store.GetAsync(f.Caller, root.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        Assert.True(current.Value.Quest.CancellationConverged);

        var tree = await TreeReadAsync(f, root.Quest.Id, new());

        Assert.True(tree.IsSuccess, tree.Error.Message);

        var parent = tree.Value.Items.Single(item => item.Id == root.Quest.Id);

        Assert.True(parent.CancellationConverged);

        Assert.Equal(canceled ? QuestState.Canceled : QuestState.Failed, parent.State);

        Assert.Equal(current.Value.Quest.ReconciliationRequired, parent.ReconciliationRequired);
    }
}
