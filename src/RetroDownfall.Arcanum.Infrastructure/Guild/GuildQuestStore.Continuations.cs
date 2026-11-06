using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildQuestStored>> WaitForInputAsync(GuildQuestDriver driver,
        Guid questionId, Guid continuationId, CancellationToken ct)
    {
        if (!DriverShape(driver) || questionId == Guid.Empty || continuationId == Guid.Empty)
        {
            return Task.FromResult(Result<GuildQuestStored>.Failure(InvalidReceipt));
        }

        return authority.InQuestTransactionAsync<GuildQuestStored>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var visible = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, false, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                GuildQuestStored row = visible.Value.Row;

                await using SqliteCommand question = Command(connection, transaction,
                    "SELECT QuestionId,ContinuationId FROM guild_quests WHERE Id=$id");

                Add(question, "$id", Id(row.Quest.Id));

                await using (SqliteDataReader reader = await question.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable;

                    if (visible.Value.Operation.State == LongRunningOperationState.Waiting && row.Quest.State == QuestState.InputRequired)
                    {
                        return !reader.IsDBNull(0) && !reader.IsDBNull(1) && reader.GetString(0) == Id(questionId)
                            && reader.GetString(1) == Id(continuationId) ? row : Conflict;
                    }
                }

                var driverCheck = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (driverCheck.IsFailure) return driverCheck.Error;

                if (driverCheck.Value.Operation.State != LongRunningOperationState.Running || row.Quest.ReconciliationRequired
                    || row.Quest.ResultReceiptId is not null
                    || await HasClosedAncestorAsync(connection, transaction, row.Quest.Id, token).ConfigureAwait(false)) return ClosedDispatch;

                if (row.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                await using SqliteCommand evidence = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$id AND EvidenceCode=2)
                        OR EXISTS(SELECT 1 FROM guild_continuations WHERE QuestId=$id AND ContinuationId=$continuation)
                    """);

                Add(evidence, "$id", Id(row.Quest.Id));

                Add(evidence, "$continuation", Id(continuationId));

                if (Equals(1L, await evidence.ExecuteScalarAsync(token).ConfigureAwait(false))) return UncertainDispatch;

                DateTimeOffset now = time.GetUtcNow();

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, row.Quest.OperationId,
                    driver.OperationRevision, driver.LeaseOwner, LongRunningOperationState.Waiting, now, null, token).ConfigureAwait(false)) return RevisionConflict;

                await using SqliteCommand wait = Command(connection, transaction, """
                    UPDATE guild_quests SET LastNonterminalStateCode=2,WaitReasonCode=0,QuestionId=$question,
                        ContinuationId=$continuation,Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision
                    """);

                Add(wait, "$id", Id(row.Quest.Id));

                Add(wait, "$revision", row.Quest.Revision);

                Add(wait, "$question", Id(questionId));

                Add(wait, "$continuation", Id(continuationId));

                Add(wait, "$now", UtcInstantText.Format(now));

                if (await wait.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                return await ReadWithinAsync(connection, transaction, row.Quest.Id, token).ConfigureAwait(false);
            }, ct);
    }

    internal async Task<Result<GuildQuestStored>> AcceptContinuationAsync(GuildCallerContext caller,
        Guid questId, GuildContinueRequest request, CancellationToken ct, ArtifactSensitivityLabel? sourceLabel = null)
    {
        if (caller is null || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run) || questId == Guid.Empty || request is null || request.IdempotencyKey == Guid.Empty
            || request.ContinuationId == Guid.Empty || request.ExpectedRevision < 1 || request.Body is null
            || request.MimeType is not ("text/plain" or "application/json")) return InvalidReceipt;

        byte[] body;

        try
        {
            UTF8Encoding encoding = new(false, true);

            if (encoding.GetByteCount(request.Body) > GuildStore.MaximumContentBytes)
                return new Error(ErrorCodes.Guild.PayloadTooLarge, "The continuation body exceeds its encoded representation bound.");

            body = encoding.GetBytes(request.Body);
        }
        catch (EncoderFallbackException)
        {
            return InvalidReceipt;
        }

        string intent = ContinuationIntent(request, body);

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildQuestStored>(located.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                GuildQuestStored row = found.Value;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, row, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (caller.Kind == GuildCallerKind.Run && caller.Execution?.QuestId != row.Quest.ParentQuestId)
                    return new Error(ErrorCodes.Guild.Forbidden, "Only the issuing agent or an authorized operator may answer this question.");

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> issuer = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (issuer.IsFailure) return issuer.Error;
                }

                await using SqliteCommand prior = Command(connection, transaction, """
                    SELECT ContinuationId,MutationId,CASE WHEN typeof(Digest)='blob' AND length(Digest)=32 THEN Digest ELSE NULL END,
                        AcceptedRevision,CreatedAtUtc FROM guild_continuations
                    WHERE QuestId=$id AND (ContinuationId=$continuation OR MutationId=$mutation)
                    """);

                Add(prior, "$id", Id(questId));

                Add(prior, "$continuation", Id(request.ContinuationId));

                Add(prior, "$mutation", Id(request.IdempotencyKey));

                await using (SqliteDataReader reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(2)) return Unavailable;

                        if (reader.GetString(0) != Id(request.ContinuationId) || reader.GetString(1) != Id(request.IdempotencyKey)
                            || Convert.ToHexString((byte[])reader.GetValue(2)) != intent) return Conflict;

                        GuildMutationReceiptDto receipt = new(request.ContinuationId, request.IdempotencyKey, true,
                            UtcInstantText.Parse(reader.GetString(4)), reader.GetInt64(3));

                        return row with { Quest = row.Quest with { Receipt = receipt } };
                    }
                }

                if (row.Quest.Revision != request.ExpectedRevision) return RevisionConflict;

                if (row.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                if (row.Quest.State != QuestState.InputRequired || row.Quest.ResultReceiptId is not null
                    || await HasClosedAncestorAsync(connection, transaction, questId, token).ConfigureAwait(false)) return ClosedDispatch;

                Result<GuildGrant> currentGrant = await signets.ValidateCurrentGrantAsync(questId, token).ConfigureAwait(false);

                if (currentGrant.IsFailure) return currentGrant.Error;

                LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction,
                    row.Quest.OperationId, token).ConfigureAwait(false);

                if (operation is null) return Unavailable;

                bool safeInputPark = operation.State == LongRunningOperationState.ReconciliationRequired
                    && operation.TerminalErrorCode == ErrorCodes.Guild.AwaitingInput;

                if (operation.State != LongRunningOperationState.Waiting && !safeInputPark
                    || row.Quest.ReconciliationRequired && !safeInputPark) return UncertainDispatch;

                await using SqliteCommand offered = Command(connection, transaction, """
                    SELECT EXISTS(SELECT 1 FROM guild_quests WHERE Id=$id AND QuestionId IS NOT NULL AND ContinuationId=$continuation AND WaitReasonCode=0)
                        AND NOT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$id AND EvidenceCode=2)
                    """);

                Add(offered, "$id", Id(questId));

                Add(offered, "$continuation", Id(request.ContinuationId));

                if (!Equals(1L, await offered.ExecuteScalarAsync(token).ConfigureAwait(false))) return UncertainDispatch;

                DateTimeOffset now = time.GetUtcNow();

                Guid contentId = Guid.NewGuid();

                Result<GuildDataRef> content = await GuildStore.WriteContentWithinAsync(connection, transaction,
                    new(contentId, request.ContinuationId, GuildContentKind.CircleBody, 1, row.Quest.SessionId, row.Quest.CampaignId,
                        request.MimeType, body, Sensitivity(contentId, row.Quest.SessionId, row.Quest.CampaignId, body, sourceLabel)), now, token).ConfigureAwait(false);

                if (content.IsFailure) return content.Error;

                await using SqliteCommand accept = Command(connection, transaction, """
                    INSERT INTO guild_continuations(QuestId,ContinuationId,MutationId,Digest,ContentId,AcceptedRevision,CreatedAtUtc)
                    VALUES($id,$continuation,$mutation,$digest,$content,$revision+1,$now);
                    UPDATE guild_quests SET LastNonterminalStateCode=1,WaitReasonCode=NULL,QuestionId=NULL,ContinuationId=NULL,
                        Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision;
                    """);

                Add(accept, "$id", Id(questId));

                Add(accept, "$continuation", Id(request.ContinuationId));

                Add(accept, "$mutation", Id(request.IdempotencyKey));

                Add(accept, "$digest", Convert.FromHexString(intent));

                Add(accept, "$content", Id(contentId));

                Add(accept, "$revision", row.Quest.Revision);

                Add(accept, "$now", UtcInstantText.Format(now));

                if (await accept.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2) return RevisionConflict;

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id, operation.Revision,
                    null, LongRunningOperationState.Running, now, null, token).ConfigureAwait(false)) return RevisionConflict;

                Result<GuildQuestStored> accepted = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (accepted.IsFailure) return accepted.Error;

                return accepted.Value with { Quest = accepted.Value.Quest with
                {
                    Receipt = new(request.ContinuationId, request.IdempotencyKey, false, now, row.Quest.Revision + 1)
                } };
            }, ct).ConfigureAwait(false);
    }

    private static string ContinuationIntent(GuildContinueRequest request, byte[] body)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            writer.WriteNumber("version", 1);

            writer.WriteString("continuationId", request.ContinuationId);

            writer.WriteNumber("expectedRevision", request.ExpectedRevision);

            writer.WriteString("mimeType", request.MimeType);

            writer.WriteString("bodyDigest", Convert.ToHexString(SHA256.HashData(body)));

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }
}
