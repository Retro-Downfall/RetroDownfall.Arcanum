using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    /// <summary>The authorized Cohort transaction seals a subtree without replacing its primary result.</summary>
    internal async Task<Result> RequestCohortCancellationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        Guid root;
        Guid operationId;
        long revision;
        bool requested;

        await using (SqliteCommand read = Command(connection, transaction, """
            SELECT RootQuestId,OperationId,Revision,CancellationRequested,
                CASE WHEN typeof(RootQuestId)='text' AND length(CAST(RootQuestId AS BLOB))=36
                  AND typeof(OperationId)='text' AND length(CAST(OperationId AS BLOB))=32
                  AND typeof(Revision)='integer' AND Revision>=1
                  AND typeof(CancellationRequested)='integer' AND CancellationRequested IN(0,1)
                  THEN 1 ELSE 0 END
            FROM guild_quests WHERE Id=$quest
            """))
        {
            Add(read, "$quest", Id(questId));

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(4) != 1
                || !Guid.TryParseExact(reader.GetString(0), "D", out root) || root == Guid.Empty
                || !Guid.TryParseExact(reader.GetString(1), "N", out operationId) || operationId == Guid.Empty)
                return Unavailable;

            revision = reader.GetInt64(2);
            requested = reader.GetInt64(3) == 1;
        }

        if (requested) return Result.Success();

        if (revision == long.MaxValue) return ExhaustedReceipt;

        LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operationId, ct).ConfigureAwait(false);

        if (operation is null) return Unavailable;

        Guid key = Guid.NewGuid();

        GuildCancelQuestRequest request = new(key, revision, GuildCancellationReason.OperatorRequested);

        Result<string> intent = GuildQuestIntent.Compute(questId, request);

        if (intent.IsFailure) return intent.Error;

        DateTimeOffset now = time.GetUtcNow();

        if (operation.State is LongRunningOperationState.Pending or LongRunningOperationState.Running or LongRunningOperationState.Waiting)
        {
            if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                operation.Revision, operation.LeaseOwner, LongRunningOperationState.Cancelling, now, null, ct).ConfigureAwait(false))
                return RevisionConflict;
        }

        await using SqliteCommand update = Command(connection, transaction, """
            UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1,UpdatedAtUtc=$now
            WHERE Id=$quest AND Revision=$expected AND CancellationRequested=0;
            INSERT INTO guild_quest_cancellations(QuestId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,
                ReasonCode,AcceptedRevision,AcceptedAtUtc) VALUES($quest,$key,$digest,$expected,$reason,$accepted,$now)
            """);

        Add(update, "$quest", Id(questId));
        Add(update, "$expected", revision);
        Add(update, "$now", UtcInstantText.Format(now));
        Add(update, "$key", Id(key));
        Add(update, "$digest", Convert.FromHexString(intent.Value));
        Add(update, "$reason", (int)request.ReasonCode);
        Add(update, "$accepted", revision + 1);

        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 2) return RevisionConflict;

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, root, questId,
            GuildEventKind.QuestChanged, questId, revision + 1, null, now, ct).ConfigureAwait(false);

        return projected.IsSuccess ? Result.Success() : Result.Failure(projected.Error);
    }
}
