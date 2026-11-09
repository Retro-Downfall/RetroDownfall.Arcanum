using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableFact]
    public async Task Interrupted_canceled_owner_cleanup_preserves_intent_receipts_and_unsettled_reset_obligations()
    {
        RequireSqlCipher();

        (Guid session, _) = await SeedSessionAsync(pinned: false);

        await SeedRetentionQuestAsync(session);

        await ExecuteAsync("""
            UPDATE guild_quests SET CancellationRequested=1,CancellationConverged=1,AccountingStateCode=2,Revision=Revision+1;
            UPDATE LongRunningOperations SET TerminalErrorCode='operation.cancelled';
            INSERT INTO guild_quest_cancellations(QuestId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,ReasonCode,AcceptedRevision,AcceptedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','33333333-3333-3333-3333-333333333333',zeroblob(32),1,0,2,'2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),5,'operation.cancelled',1,1,'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',2,'2000-01-01T00:00:00.0000000Z');
            INSERT INTO guild_result_outbox(QuestId,ResultId,Digest,DeliveryStateCode,Revision,CreatedAtUtc)
            VALUES('11111111-1111-1111-1111-111111111111','EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',zeroblob(32),3,1,'2000-01-01T00:00:00.0000000Z');
            """);

        await DeleteGuildTestOwnerAsync();

        GuildQuestCleanupParticipant questCleanup = new(FixtureOrdinaryConnectionFactory.For(_db!), TimeProvider.System);

        using CancellationTokenSource interruption = new();

        var interrupted = GuildCleanup([new InterruptAfterGuildSeal(interruption), questCleanup]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted.RunBatchAsync(8, 8, interruption.Token));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_owner_cleanup WHERE PhaseCode=1"));

        Assert.Equal(0, await CampaignScalarCountAsync("SELECT AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2"));

        var resumed = await GuildCleanup([questCleanup]).RunBatchAsync(8, 8, default);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, resumed.Error.Code);

        DataRetentionPlan reset = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        Assert.Contains(reset.Conflicts, conflict => conflict.Code == ErrorCodes.Guild.ReconciliationRequired);

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_quests WHERE Retired=1 AND CancellationRequested=1 AND AccountingStateCode=2"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_quest_cancellations WHERE ExpectedRevision=1 AND AcceptedRevision=2"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_result_receipts WHERE OutcomeCode=5 AND WorkStoppedProven=1"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM guild_result_outbox WHERE DeliveryStateCode=3"));

        Assert.Equal(1, await CampaignScalarCountAsync("SELECT count(*) FROM LongRunningOperations WHERE TerminalErrorCode='operation.cancelled'"));
    }

    private sealed class InterruptAfterGuildSeal(CancellationTokenSource interruption) : IGuildOwnerCleanupParticipant
    {
        public ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
        {
            interruption.Cancel();

            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());
    }
}
