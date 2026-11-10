using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildRemoteTaskIdentity(string TaskId, string? ContextId);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildRemoteTaskIdentity>> ReadRemoteTaskIdentityAsync(GuildQuestDriver driver, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildRemoteTaskIdentity>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                return await ReadRemoteTaskIdentityWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);
            }, ct);

    private static async Task<Result<GuildRemoteTaskIdentity>> ReadRemoteTaskIdentityWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken token)
    {
        await using SqliteCommand query = Command(connection, transaction, """
            SELECT RemoteTaskId,RemoteContextId FROM guild_dispatches WHERE QuestId=$quest AND RemoteTaskId IS NOT NULL
            ORDER BY CreatedAtUtc,DispatchId
            """);

        Add(query, "$quest", Id(questId));

        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);

        GuildRemoteTaskIdentity? identity = null;

        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var candidate = new GuildRemoteTaskIdentity(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));

            if (identity is not null && identity != candidate) return UncertainDispatch;

            identity = candidate;
        }

        return identity is null ? UncertainDispatch : identity;
    }

    internal async Task<Result> RecordRemoteCallbackAsync(GuildQuestDriver driver, string configId, string tokenHash, CancellationToken ct, Guid? dispatchId = null)
    {
        if (!A2ACallbackConfigId.IsWellFormed(configId) || tokenHash is not { Length: 64 }
            || tokenHash.Any(value => !Uri.IsHexDigit(value))) return InvalidReceipt;

        string digest = Convert.ToHexString(Convert.FromHexString(tokenHash));

        Guid acceptedDispatch = dispatchId ?? driver.Execution.QuestId;

        var recorded = await authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var input = await ReadRemoteInvocationWithinAsync(connection, transaction, driver.Execution.QuestId,
                    acceptedDispatch, token).ConfigureAwait(false);

                if (input.IsFailure) return input.Error;

                if (input.Value.Mode != A2ADispatchMode.Callback) return Conflict;

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, acceptedDispatch, token).ConfigureAwait(false);

                if (dispatch.IsFailure || dispatch.Value is not { Evidence: GuildDispatchEvidence.Confirmed, RemoteTaskId: not null } current
                    || current.QuestId != driver.Execution.QuestId || current.LocalChildId is not null) return Conflict;

                await using SqliteCommand command = Command(connection, transaction, """
                    INSERT INTO guild_remote_callbacks(CallbackConfigId,QuestId,DispatchId,CallbackTokenHash)
                    SELECT $config,$quest,$dispatch,$hash WHERE EXISTS(
                        SELECT 1 FROM guild_remote_effects r JOIN guild_dispatches original ON original.DispatchId=r.DispatchId
                        WHERE r.QuestId=$quest AND original.RemoteTaskId=$task
                            AND (original.RemoteContextId IS NULL OR original.RemoteContextId=$context))
                    ON CONFLICT(CallbackConfigId) DO NOTHING
                    """);

                Add(command, "$quest", Id(driver.Execution.QuestId));
                Add(command, "$dispatch", Id(acceptedDispatch));
                Add(command, "$task", current.RemoteTaskId);
                Add(command, "$context", current.RemoteContextId);
                Add(command, "$config", configId);
                Add(command, "$hash", digest);

                _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                command.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM guild_remote_callbacks
                        WHERE CallbackConfigId=$config AND QuestId=$quest AND DispatchId=$dispatch AND CallbackTokenHash=$hash)
                    """;

                return Equals(1L, await command.ExecuteScalarAsync(token).ConfigureAwait(false)) ? true : Conflict;
            }, ct).ConfigureAwait(false);

        return recorded.IsFailure ? recorded.Error : Result.Success();
    }

    internal async Task<Result> WaitForRemoteInputAsync(GuildQuestDriver driver, Guid dispatchId,
        A2AGuildTaskObservation observation, CancellationToken ct)
    {
        var waiting = await authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var input = await ReadRemoteInvocationWithinAsync(connection, transaction, driver.Execution.QuestId, dispatchId, token).ConfigureAwait(false);

                if (input.IsFailure) return input.Error;

                if (input.Value.Mode != A2ADispatchMode.Continuable || observation.TaskId is null)
                    return new Error(ErrorCodes.Sending.TaskRejected, "The peer needs input that this blocking Sending cannot supply.");

                GuildQuestStored row = admitted.Value.Row;

                Result physical = await ApplyRemoteEvidenceWithinAsync(connection, transaction, row, new(dispatchId, observation), false, token).ConfigureAwait(false);

                if (physical.IsFailure) return physical.Error;

                byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes($"Guild.Remote.Question:{dispatchId:D}"));

                Guid question = new(identity.AsSpan(0, 16));

                Guid continuation = new(SHA256.HashData(identity).AsSpan(0, 16));

                byte[] body = Encoding.UTF8.GetBytes(observation.ResponseText);

                if (body.Length > GuildStore.MaximumContentBytes) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The remote question exceeds its representation bound.");

                bool existingQuestion;

                await using (SqliteCommand prior = Command(connection, transaction, """
                    SELECT QuestionId,ContinuationId,Digest,ByteLength,ContentId FROM guild_remote_questions
                    WHERE QuestId=$quest AND DispatchId=$dispatch
                    """))
                {
                    Add(prior, "$quest", Id(row.Quest.Id));
                    Add(prior, "$dispatch", Id(dispatchId));

                    await using SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    existingQuestion = await reader.ReadAsync(token).ConfigureAwait(false);

                    if (existingQuestion)
                    {
                        if (reader.GetString(0) != Id(question) || reader.GetString(1) != Id(continuation)
                            || !((byte[])reader.GetValue(2)).AsSpan().SequenceEqual(SHA256.HashData(body))
                            || reader.GetInt64(3) != body.Length) return UncertainDispatch;

                        var reference = new GuildDataRef(Guid.Parse(reader.GetString(4)), GuildContentKind.ExecutionCheckpoint, 1,
                            Convert.ToHexString((byte[])reader.GetValue(2)), "text/plain", body.Length, false);

                        await reader.DisposeAsync().ConfigureAwait(false);

                        var retained = await ReadProviderContentWithinAsync(connection, transaction, reference, token).ConfigureAwait(false);

                        if (retained.IsFailure) return retained.Error;

                        if (retained.Value != observation.ResponseText) return UncertainDispatch;
                    }
                }

                if (!existingQuestion)
                {
                    Guid contentId = Guid.NewGuid();

                    var label = await ResultSensitivityWithinAsync(connection, transaction, row, contentId, body, null, token).ConfigureAwait(false);

                    if (label.IsFailure) return label.Error;

                    var content = await GuildStore.WriteContentWithinAsync(connection, transaction,
                        new(contentId, question, GuildContentKind.ExecutionCheckpoint, 1, row.Quest.SessionId, row.Quest.CampaignId,
                            "text/plain", body, label.Value), time.GetUtcNow(), token).ConfigureAwait(false);

                    if (content.IsFailure) return content.Error;

                    await using (SqliteCommand insert = Command(connection, transaction, """
                        INSERT INTO guild_remote_questions(QuestId,DispatchId,QuestionId,ContinuationId,ContentId,Digest,ByteLength)
                        VALUES($quest,$dispatch,$question,$continuation,$content,$digest,$bytes)
                        """))
                    {
                        Add(insert, "$quest", Id(row.Quest.Id));
                        Add(insert, "$dispatch", Id(dispatchId));
                        Add(insert, "$question", Id(question));
                        Add(insert, "$continuation", Id(continuation));
                        Add(insert, "$content", Id(contentId));
                        Add(insert, "$digest", Convert.FromHexString(content.Value.Digest));
                        Add(insert, "$bytes", body.Length);

                        _ = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, row.Quest.OperationId,
                    driver.OperationRevision, driver.LeaseOwner, LongRunningOperationState.Waiting, time.GetUtcNow(), null, token).ConfigureAwait(false))
                    return RevisionConflict;

                bool sameQuestion = false;

                if (existingQuestion)
                {
                    // The observation lease temporarily projects Running. Compare the immutable
                    // offered question itself so polling cannot stale an unanswered revision.
                    await using SqliteCommand offered = Command(connection, transaction, """
                        SELECT EXISTS(SELECT 1 FROM guild_quests WHERE Id=$quest AND LastNonterminalStateCode=2
                            AND WaitReasonCode=0 AND QuestionId=$question AND ContinuationId=$continuation)
                        """);

                    Add(offered, "$quest", Id(row.Quest.Id));
                    Add(offered, "$question", Id(question));
                    Add(offered, "$continuation", Id(continuation));

                    sameQuestion = Equals(1L, await offered.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                if (sameQuestion)
                {
                    await using SqliteCommand release = Command(connection, transaction,
                        "UPDATE LongRunningOperations SET LeaseOwner=NULL,LeaseExpiresAt=NULL WHERE Id=$id AND State=2 AND LeaseOwner=$owner");

                    Add(release, "$id", row.Quest.OperationId.ToString("N"));
                    Add(release, "$owner", driver.LeaseOwner);

                    return await release.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1 ? true : RevisionConflict;
                }

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_quests SET LastNonterminalStateCode=2,WaitReasonCode=0,QuestionId=$question,ContinuationId=$continuation,
                        Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision;
                    UPDATE LongRunningOperations SET LeaseOwner=NULL,LeaseExpiresAt=NULL WHERE Id=$operation AND State=2 AND LeaseOwner=$owner;
                    """);

                Add(update, "$quest", Id(row.Quest.Id));
                Add(update, "$question", Id(question));
                Add(update, "$continuation", Id(continuation));
                Add(update, "$revision", row.Quest.Revision);
                Add(update, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(update, "$operation", row.Quest.OperationId.ToString("N"));
                Add(update, "$owner", driver.LeaseOwner);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, row.Quest.RootQuestId, row.Quest.Id,
                    GuildEventKind.QuestChanged, question, 1, null, time.GetUtcNow(), token, GuildPortalEventSource.Dispatch).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : true;
            }, ct).ConfigureAwait(false);

        return waiting.IsFailure ? waiting.Error : Result.Success();
    }
}
