using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private static async Task<bool> IsRemoteWithinAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using SqliteCommand kind = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_remote_executions WHERE QuestId=$quest)");

        Add(kind, "$quest", Id(questId));

        return Equals(1L, await kind.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static async Task<Result> RequireRemoteWaitFrontierWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using SqliteCommand frontier = Command(connection, transaction, """
            SELECT NOT EXISTS(SELECT 1 FROM guild_dispatches d WHERE d.QuestId=$quest AND d.EvidenceCode=2)
                AND EXISTS(SELECT 1 FROM guild_remote_effects r JOIN guild_budget_effects e ON e.EffectId=r.EffectId AND e.QuestId=r.QuestId
                    JOIN guild_dispatches d ON d.DispatchId=r.DispatchId AND d.QuestId=r.QuestId
                    WHERE r.QuestId=$quest AND e.KindCode=1 AND e.StateCode=1 AND e.SourceId=$quest
                        AND d.EvidenceCode=3 AND d.RemoteTaskId IS NOT NULL
                        AND EXISTS(SELECT 1 FROM guild_budget_evidence b WHERE b.EffectId=e.EffectId AND b.AppliedRevision=e.Revision))
                AND NOT EXISTS(SELECT 1 FROM guild_budget_effects e WHERE e.QuestId=$quest AND (e.KindCode<>1 OR e.SourceId<>$quest))
            """);

        Add(frontier, "$quest", Id(questId));

        return Equals(1L, await frontier.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Result.Failure(UncertainDispatch);
    }

    private static async Task<Result<GuildQuestInputDto>> ReadRemoteQuestionWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestDto quest, GuildQuestInputEvidence question, CancellationToken ct)
    {
        var input = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, quest.Id, ct).ConfigureAwait(false);

        if (input.IsFailure) return input.Error;

        Result frontier = await RequireRemoteWaitFrontierWithinAsync(connection, transaction, quest.Id, ct).ConfigureAwait(false);

        if (frontier.IsFailure) return frontier.Error;

        GuildDataRef reference;

        await using (SqliteCommand read = Command(connection, transaction, """
            SELECT w.ContentId,w.Digest,w.ByteLength,
                EXISTS(SELECT 1 FROM guild_content_erasure_receipts erased WHERE erased.ContentId=w.ContentId)
            FROM guild_remote_questions w JOIN guild_dispatches d ON d.DispatchId=w.DispatchId AND d.QuestId=w.QuestId
            WHERE w.QuestId=$quest AND w.QuestionId=$question AND w.ContinuationId=$continuation
                AND d.EvidenceCode=3 AND d.RemoteTaskId IS NOT NULL AND typeof(w.Digest)='blob' AND length(w.Digest)=32
                AND typeof(w.ByteLength)='integer' AND w.ByteLength BETWEEN 0 AND 65536
            """))
        {
            Add(read, "$quest", Id(quest.Id));
            Add(read, "$question", Id(question.QuestionId));
            Add(read, "$continuation", Id(question.ContinuationId));

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

            reference = new(Guid.Parse(reader.GetString(0)), GuildContentKind.ExecutionCheckpoint, 1,
                Convert.ToHexString((byte[])reader.GetValue(1)), "text/plain", reader.GetInt64(2), reader.GetBoolean(3));
        }

        if (reference.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The remote question body was erased.");

        var body = await ReadProviderContentWithinAsync(connection, transaction, reference, ct).ConfigureAwait(false);

        return body.IsFailure ? body.Error : new GuildQuestInputDto(quest.Id, quest.Revision, question, body.Value);
    }

    private static async Task<bool> HasRemoteParkedQuestionWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, GuildQuestInputEvidence question, CancellationToken ct)
    {
        if ((await RequireRemoteWaitFrontierWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false)).IsFailure) return false;

        await using SqliteCommand query = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_remote_questions w
                JOIN guild_content c ON c.ContentId=w.ContentId AND c.ContentKindCode=10 AND c.SourceRecordId=w.QuestionId
                    AND c.Revision=1 AND c.Digest=w.Digest AND length(c.Body)=w.ByteLength
                JOIN guild_remote_executions r ON r.QuestId=w.QuestId
                WHERE w.QuestId=$quest AND w.QuestionId=$question AND w.ContinuationId=$continuation
                    AND NOT EXISTS(SELECT 1 FROM guild_content_erasure_receipts e WHERE e.ContentId IN(w.ContentId,r.InputContentId)))
            """);

        Add(query, "$quest", Id(questId));
        Add(query, "$question", Id(question.QuestionId));
        Add(query, "$continuation", Id(question.ContinuationId));

        return Equals(1L, await query.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
