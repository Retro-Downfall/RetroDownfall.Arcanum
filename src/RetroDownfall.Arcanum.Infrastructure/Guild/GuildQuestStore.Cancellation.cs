using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildCancellationDto>> RequestCancellationAsync(GuildCallerContext caller,
        GuildMutationIdentity mutation, Guid questId, GuildCancelQuestRequest request, CancellationToken ct)
    {
        Result<string> intent = GuildQuestIntent.Compute(questId, request);

        if (intent.IsFailure) return intent.Error;

        if (caller is null || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run)
            || !ReceiptMutationValid(mutation) || mutation.IdempotencyKey != request.IdempotencyKey
            || mutation.CanonicalRequestDigest != intent.Value) return InvalidReceipt;

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildCancellationDto>(located.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                GuildQuestStored row = found.Value;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, row, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;

                    await using SqliteCommand lineage = Command(connection, transaction, """
                        WITH RECURSIVE ancestors(Id,ParentQuestId) AS (
                            SELECT Id,ParentQuestId FROM guild_quests WHERE Id=$quest
                            UNION
                            SELECT q.Id,q.ParentQuestId FROM guild_quests q JOIN ancestors a ON q.Id=a.ParentQuestId
                        ) SELECT EXISTS(SELECT 1 FROM ancestors WHERE Id=$caller)
                        """);

                    Add(lineage, "$quest", Id(questId));

                    Add(lineage, "$caller", Id(execution.QuestId));

                    if (!Equals(1L, await lineage.ExecuteScalarAsync(token).ConfigureAwait(false))) return OwnerUnavailable;
                }

                GuildMutationReceiptDto? receipt = null;

                await using (SqliteCommand prior = Command(connection, transaction, """
                    SELECT CASE WHEN typeof(CanonicalRequestDigest)='blob' AND length(CanonicalRequestDigest)=32 THEN CanonicalRequestDigest END,
                        ExpectedRevision,ReasonCode,AcceptedRevision,
                        CASE WHEN typeof(AcceptedAtUtc)='text' AND length(CAST(AcceptedAtUtc AS BLOB))<=64 THEN AcceptedAtUtc END
                    FROM guild_quest_cancellations WHERE QuestId=$quest AND IdempotencyKey=$key
                    """))
                {
                    Add(prior, "$quest", Id(questId));

                    Add(prior, "$key", Id(request.IdempotencyKey));

                    await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(0) || reader.IsDBNull(4) || reader.GetInt64(1) < 1
                            || reader.GetInt64(3) < reader.GetInt64(1) || reader.GetInt64(3) > row.Quest.Revision
                            || !UtcInstantText.TryParse(reader.GetString(4), out DateTimeOffset acceptedAt)) return Unavailable;

                        if (Convert.ToHexString((byte[])reader.GetValue(0)) != intent.Value
                            || reader.GetInt64(1) != request.ExpectedRevision || reader.GetInt64(2) != (int)request.ReasonCode) return Conflict;

                        receipt = new(request.IdempotencyKey, request.IdempotencyKey, true, acceptedAt, reader.GetInt64(3));
                    }
                }

                if (receipt is not null)
                    return await CancellationSnapshotWithinAsync(connection, transaction, row, receipt, token).ConfigureAwait(false);

                if (row.Quest.Revision != request.ExpectedRevision) return RevisionConflict;

                if (row.Quest.ResultReceiptId is not null) return ClosedDispatch;

                if (row.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(
                    connection, transaction, row.Quest.OperationId, token).ConfigureAwait(false);

                if (operation is null) return Unavailable;

                if (operation.State is LongRunningOperationState.Completed or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned)
                    return UncertainDispatch;

                DateTimeOffset now = time.GetUtcNow();

                // Attention remains attention: requesting a stop never proves an uncertain effect stopped.
                if (operation.State is LongRunningOperationState.Pending or LongRunningOperationState.Running or LongRunningOperationState.Waiting)
                {
                    if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                        operation.Revision, operation.LeaseOwner, LongRunningOperationState.Cancelling, now, null, token).ConfigureAwait(false))
                        return RevisionConflict;
                }

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1,UpdatedAtUtc=$now
                    WHERE Id=$quest AND Revision=$expected;
                    INSERT INTO guild_quest_cancellations(QuestId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,
                        ReasonCode,AcceptedRevision,AcceptedAtUtc) VALUES($quest,$key,$digest,$expected,$reason,$accepted,$now)
                    """);

                Add(update, "$quest", Id(questId));

                Add(update, "$expected", request.ExpectedRevision);

                Add(update, "$now", UtcInstantText.Format(now));

                Add(update, "$key", Id(request.IdempotencyKey));

                Add(update, "$digest", Convert.FromHexString(intent.Value));

                Add(update, "$reason", (int)request.ReasonCode);

                Add(update, "$accepted", row.Quest.Revision + 1);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2) return RevisionConflict;

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    row.Quest.RootQuestId, questId, GuildEventKind.QuestChanged, questId, row.Quest.Revision + 1, null, now, token).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                Result<GuildQuestStored> current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                return current.IsFailure ? current.Error : await CancellationSnapshotWithinAsync(connection, transaction, current.Value,
                    new(request.IdempotencyKey, request.IdempotencyKey, false, now, row.Quest.Revision + 1), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    private static async Task<Result<GuildCancellationDto>> CancellationSnapshotWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored row, GuildMutationReceiptDto receipt, CancellationToken ct)
    {
        if (!row.Quest.CancellationRequested) return Unavailable;

        await using SqliteCommand counts = Command(connection, transaction, $"""
            WITH RECURSIVE descendants(Id) AS (
                SELECT Id FROM guild_quests WHERE Id=$quest
                UNION SELECT q.Id FROM guild_quests q JOIN descendants d ON q.ParentQuestId=d.Id
            ) SELECT
                (SELECT count(*) FROM descendants d JOIN guild_quests c ON c.Id=d.Id WHERE {GuildQuestRetentionPins.StoppedExecutionUnresolved}),
                (SELECT count(*) FROM guild_budget_effects e JOIN descendants d ON d.Id=e.QuestId WHERE e.StateCode IN(0,1))
                + (SELECT count(*) FROM guild_dispatches p JOIN descendants d ON d.Id=p.QuestId WHERE p.EvidenceCode=2
                    AND NOT EXISTS(SELECT 1 FROM guild_budget_effects e WHERE e.DispatchId=p.DispatchId))
                + (SELECT count(*) FROM guild_workspace_leases lease JOIN descendants d ON d.Id=lease.QuestId
                    WHERE lease.StateCode IS NOT 3 OR lease.InFlightMutationCount IS NOT 0)
            """);

        Add(counts, "$quest", Id(row.Quest.Id));

        await using SqliteDataReader reader = await counts.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

        return new GuildCancellationDto(row.Quest.Id, row.Quest.RootQuestId, row.Quest.Revision, true,
            row.Quest.CancellationConverged, reader.GetInt64(0), reader.GetInt64(1), row.Quest.AccountingState,
            row.Quest.EventWatermark, receipt);
    }
}
