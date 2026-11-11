using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildQuestStored>> GetAsync(GuildCallerContext caller, Guid questId, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestStored>(caller.Execution?.RootQuestId ?? questId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);
                if (found.IsFailure) return found.Error;
                Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);
                if (visible.IsFailure) return visible.Error;
                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);
                    if (live.IsFailure) return live.Error;
                }

                return found;
            }, ct);

    internal async Task<Result> RequireVisibleAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildQuestStored row, CancellationToken ct)
    {
        if (caller.InstallationGeneration != policy.InstallationGeneration || row.Issuance.InstallationGeneration != policy.InstallationGeneration || row.Retired)
            return OwnerUnavailable;
        Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, row.Quest.SessionId, row.Quest.CampaignId, ct).ConfigureAwait(false);
        if (owner.IsFailure) return owner;
        return await policy.AuthorizeLocateAsync(caller, connection, transaction, row.Quest.Id,
            row.Quest.SessionId, row.Quest.CampaignId, ct).ConfigureAwait(false);
    }

    internal static async Task<Result<GuildQuestStored>> ReadWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken ct)
    {
        try
        {
            Result bounded = await RequireBoundedReadWithinAsync(connection, transaction, id, ct).ConfigureAwait(false);

            return bounded.IsFailure ? bounded.Error : await ReadBoundedWithinAsync(connection, transaction, id, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidCastException or OverflowException)
        {
            return Unavailable;
        }
    }

    private static async Task<Result<GuildQuestStored>> ReadBoundedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken ct)
    {
        GuildQuestDto dto;
        GuildIssuanceIdentity issuance;
        Guid mutation, account;
        string intent, resolution;
        bool retired;
        QuestState last;
        GuildQuestInputEvidence? question;
        await using (SqliteCommand command = Command(connection, transaction, "SELECT * FROM guild_quests WHERE Id=$id"))
        {
            Add(command, "$id", Id(id));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.NotFound, "The Quest is unavailable.");
            string S(string column) => reader.GetString(reader.GetOrdinal(column));
            string? N(string column) => reader.IsDBNull(reader.GetOrdinal(column)) ? null : S(column);
            Guid G(string column) => Guid.Parse(S(column));
            Guid? O(string column) => N(column) is { } value ? Guid.Parse(value) : null;
            long L(string column) => reader.GetInt64(reader.GetOrdinal(column));
            string H(string column) => Convert.ToHexString((byte[])reader.GetValue(reader.GetOrdinal(column)));
            last = (QuestState)L("LastNonterminalStateCode");
            GuildDataRef goal = new(G("GoalContentId"), GuildContentKind.QuestGoal, L("GoalRevision"), H("GoalDigest"), S("GoalMimeType"), L("GoalByteLength"), false);
            mutation = G("MutationId");
            account = G("AllocationAccountId");
            intent = H("IntentDigest");
            resolution = H("ResolutionDigest");
            retired = L("Retired") != 0;
            issuance = new(G("InstallationGeneration"), L("IssuerKindCode") == 1 ? G("IssuerIdentity") : null,
                L("IssuerKindCode") == 0 ? G("IssuerIdentity") : null, G("IssuingStepId"), G("LogicalCallId"));
            question = O("QuestionId") is { } q && O("ContinuationId") is { } continuation ? new(q, continuation) : null;
            DateTimeOffset created = UtcInstantText.Parse(S("CreatedAtUtc"));
            dto = new(id, N("Name"), G("OperationId"), G("AgentId"), G("CardId"), L("CardRevision"), G("RunId"),
                O("ParentQuestId"), G("RootQuestId"), O("SessionId"), O("CampaignId"), O("WorkspaceId"), G("SignetId"), 0,
                L("Revision"), L("IssuedSequence"), last, reader.IsDBNull(reader.GetOrdinal("WaitReasonCode")) ? null : (GuildWaitReason)L("WaitReasonCode"),
                L("CancellationRequested") != 0, L("CancellationConverged") != 0, false, GuildDeliveryState.None, (GuildAccountingState)L("AccountingStateCode"),
                goal, null, null, null!, created, UtcInstantText.Parse(S("UpdatedAtUtc")), 0, new(id, mutation, false, created, 1));
        }
        LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, dto.OperationId, ct).ConfigureAwait(false);
        if (operation is null || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete
            || operation.CheckpointReference != Id(id))
            return Unavailable;

        var checkpoint = GuildQuestCheckpoint.Decode(operation.CheckpointVersion, operation.CheckpointPayload);

        if (checkpoint.IsFailure || checkpoint.Value.QuestId != id)
            return Unavailable;

        Result<GuildSignetRecord> signet = await GuildSignetStore.ReadAsync(connection, transaction, dto.SignetId, false, ct).ConfigureAwait(false);
        if (operation is null || operation.Kind != LongRunningOperationKinds.GuildQuest || operation.RunId != dto.RunId || operation.SessionId != dto.SessionId
            || signet.IsFailure || signet.Value.Binding.QuestId != id || signet.Value.Binding.RunId != dto.RunId
            || signet.Value.Binding.AgentId != dto.AgentId || signet.Value.Binding.RootQuestId != dto.RootQuestId
            || signet.Value.Owners.SessionId != dto.SessionId || signet.Value.Owners.CampaignId != dto.CampaignId
            || signet.Value.Owners.WorkspaceId != dto.WorkspaceId || signet.Value.InstallationGeneration != issuance.InstallationGeneration)
            return Unavailable;
        Result<GuildDataRef> goalRef = await ResolveContentReferenceAsync(connection, transaction, dto.GoalRef, dto.SessionId, dto.CampaignId, id, ct).ConfigureAwait(false);
        if (goalRef.IsFailure) return goalRef.Error;
        GuildResultReceipt? result = null;
        bool noDispatch = false, stopped = false;
        await using (SqliteCommand receipt = Command(connection, transaction, "SELECT ResultId,Digest,ContentId,ContentRevision,MimeType,ByteLength,OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven FROM guild_result_receipts WHERE QuestId=$id"))
        {
            Add(receipt, "$id", Id(id));
            await using SqliteDataReader reader = await receipt.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string hash = Convert.ToHexString((byte[])reader.GetValue(1));
                GuildDataRef? content = reader.IsDBNull(2) ? null : new(Guid.Parse(reader.GetString(2)), GuildContentKind.QuestResult,
                    reader.GetInt64(3), hash, reader.GetString(4), reader.GetInt64(5), false);
                result = new(id, Guid.Parse(reader.GetString(0)), hash, content, (QuestState)reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7));
                noDispatch = reader.GetBoolean(8);
                stopped = reader.GetBoolean(9);
            }
        }

        if (result?.Content is { } resultContent)
        {
            Result<GuildDataRef> reference = await ResolveContentReferenceAsync(connection, transaction, resultContent, dto.SessionId, dto.CampaignId, result.ResultId, ct).ConfigureAwait(false);
            if (reference.IsFailure) return reference.Error;
            result = result with { Content = reference.Value };
        }
        GuildDeliveryState delivery = GuildDeliveryState.None;
        await using (SqliteCommand outbox = Command(connection, transaction, "SELECT DeliveryStateCode FROM guild_result_outbox WHERE QuestId=$id"))
        {
            Add(outbox, "$id", Id(id));
            if (await outbox.ExecuteScalarAsync(ct).ConfigureAwait(false) is long value) delivery = (GuildDeliveryState)value;
        }

        bool convergence = (dto.CancellationRequested || operation.State == LongRunningOperationState.Cancelling
                || operation.TerminalErrorCode == LongRunningOperationRecoveryOutcomes.Cancelled)
            && await ConvergenceSettledWithinAsync(connection, transaction, id, ct).ConfigureAwait(false);

        GuildQuestProjectedState projection = GuildQuestProjection.Project(operation,
            new(id, dto.OperationId, last, dto.WaitReason, question, result, noDispatch, stopped,
                operation.State == LongRunningOperationState.Abandoned && result?.Outcome == QuestState.Failed, dto.CancellationRequested, convergence));

        // A stopped input wait remains reconciliation work, but its proven question can still
        // be read and answered. Never infer this permission from a generic attention state.
        bool parkedQuestion = operation.State == LongRunningOperationState.ReconciliationRequired
            && operation.TerminalErrorCode == ErrorCodes.Guild.AwaitingInput
            && last == QuestState.InputRequired && dto.WaitReason == GuildWaitReason.Input
            && question is not null && result is null && !projection.CancellationRequested && !retired
            && !await HasClosedAncestorAsync(connection, transaction, id, ct).ConfigureAwait(false)
            && (await RequireWaitFrontierWithinAsync(connection, transaction, id, ct).ConfigureAwait(false)).IsSuccess
            && await HasParkedInputCheckpointWithinAsync(connection, transaction, id, question, ct).ConfigureAwait(false);

        ToolNameDiagnostic[]? toolDiagnostics = null;

        bool legacyPark = operation.State == LongRunningOperationState.ReconciliationRequired
            && operation.TerminalErrorCode == ErrorCodes.Guild.LegacyToolName && result is null;

        if (legacyPark)
        {
            var diagnosed = await ReadWaitToolDiagnosticsWithinAsync(connection, transaction, id, question?.ContinuationId, ct).ConfigureAwait(false);

            if (diagnosed.IsFailure) return diagnosed.Error;

            toolDiagnostics = diagnosed.Value;
        }

        dto = dto with
        {
            GoalRef = goalRef.Value, ResultRef = result?.Content, ResultReceiptId = result?.ResultId, ResolvedGrant = signet.Value.Binding.Grant,
            SignetRevision = signet.Value.Revision, State = projection.State, WaitReason = projection.WaitReason,
            CancellationRequested = projection.CancellationRequested, CancellationConverged = projection.CancellationConverged,
            ReconciliationRequired = projection.ReconciliationRequired, DeliveryState = delivery,
            AwaitingInput = projection.State == QuestState.InputRequired && (!projection.ReconciliationRequired || parkedQuestion) ? question : null,
            ReasonCode = legacyPark ? ErrorCodes.Guild.LegacyToolName : null,
            ToolDiagnostics = toolDiagnostics,
        };
        return new GuildQuestStored(dto, issuance, mutation, intent, resolution, account, retired);
    }

    internal static async Task<bool> HasParkedInputCheckpointWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, GuildQuestInputEvidence question, CancellationToken ct)
    {
        if (await IsRemoteWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false))
            return await HasRemoteParkedQuestionWithinAsync(connection, transaction, questId, question, ct).ConfigureAwait(false);

        await using SqliteCommand checkpoint = Command(connection, transaction, $"""
            SELECT EXISTS(SELECT 1 FROM guild_local_wait_checkpoints w
                JOIN guild_local_executions b ON b.QuestId=w.QuestId
                WHERE w.QuestId=$quest AND w.QuestionId=$question AND w.ContinuationId=$continuation
                    AND {WaitCheckpointProof} AND {GuildLocalExecutionStore.BindingProof})
            """);

        Add(checkpoint, "$quest", Id(questId));

        Add(checkpoint, "$question", Id(question.QuestionId));

        Add(checkpoint, "$continuation", Id(question.ContinuationId));

        return Equals(1L, await checkpoint.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    internal static async Task<Result> RequireBoundedReadWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, CancellationToken ct, Guid? expectedOperationId = null)
    {
        // Installed-schema health does not prove that retained rows still obey their CHECKs.
        // Inspect sizes/types in SQLite before any provider getter allocates their managed representation.
        List<string> predicates =
        [
            BoundedText("c.Name", 65_536, true),
            BoundedText("c.GoalMimeType", 65_536),
            BoundedText("c.CreatedAtUtc", 64),
            BoundedText("c.UpdatedAtUtc", 64),
            FixedDigest("c.IntentDigest"),
            FixedDigest("c.ResolutionDigest"),
            FixedDigest("c.GoalDigest"),
            "o.Id IS NOT NULL",
            "o.Kind='guild-quest'",
            "typeof(o.RecoveryPolicy)='integer' AND o.RecoveryPolicy=2",
            "typeof(o.CheckpointVersion)='integer' AND o.CheckpointVersion=1",
            "typeof(o.CheckpointPayload)='blob' AND length(o.CheckpointPayload) BETWEEN 1 AND 512",
            "typeof(o.CheckpointReference)='text' AND o.CheckpointReference=c.Id",
            BoundedText("o.PublicSummary", 4_096),
            BoundedText("o.LeaseOwner", 800, true),
            BoundedText("o.TerminalErrorCode", 800, true),
            BoundedText("o.CreatedAt", 64),
            "typeof(c.CardRevision)='integer' AND c.CardRevision>=1",
            "typeof(c.GoalRevision)='integer' AND c.GoalRevision>=1",
            "typeof(c.GoalByteLength)='integer' AND c.GoalByteLength BETWEEN 0 AND 65536",
            "typeof(c.IssuedSequence)='integer' AND c.IssuedSequence>=1",
            "typeof(c.Revision)='integer' AND c.Revision>=1",
            "typeof(c.IssuerKindCode)='integer' AND c.IssuerKindCode IN(0,1)",
            "typeof(c.LastNonterminalStateCode)='integer' AND c.LastNonterminalStateCode IN(0,1,2)",
            "c.WaitReasonCode IS NULL OR (typeof(c.WaitReasonCode)='integer' AND c.WaitReasonCode IN(0,1,2,3,4))",
            "typeof(c.CancellationRequested)='integer' AND c.CancellationRequested IN(0,1)",
            "typeof(c.CancellationConverged)='integer' AND c.CancellationConverged IN(0,1)",
            "typeof(c.Retired)='integer' AND c.Retired IN(0,1)",
            "typeof(c.AccountingStateCode)='integer' AND c.AccountingStateCode IN(0,1,2)",
            "typeof(o.State)='integer' AND o.State BETWEEN 0 AND 7",
            "typeof(o.Revision)='integer' AND o.Revision>=0",
            "typeof(o.AttemptCount)='integer' AND o.AttemptCount BETWEEN 0 AND 2147483647",
        ];

        foreach (string column in new[] { "Id", "OperationId", "InstallationGeneration", "IssuerIdentity", "IssuingStepId", "LogicalCallId", "MutationId",
            "RootQuestId", "AgentId", "CardId", "RunId", "SignetId", "GoalContentId", "AllocationAccountId" })
            predicates.Add(BoundedText("c." + column, 36));

        foreach (string column in new[] { "ParentQuestId", "SessionId", "CampaignId", "WorkspaceId", "QuestionId", "ContinuationId" })
            predicates.Add(BoundedText("c." + column, 36, true));

        foreach (string column in new[] { "Id", "RunId", "SessionId", "RootOperationId", "ParentOperationId", "InferenceRunId", "BudgetReservationId", "IdempotencyClaimId" })
            predicates.Add(BoundedText("o." + column, 36, column != "Id"));

        foreach (string column in new[] { "StartedAt", "HeartbeatAt", "CompletedAt", "LeaseExpiresAt" })
            predicates.Add(BoundedText("o." + column, 64, true));

        predicates.Add("""
            NOT EXISTS(SELECT 1 FROM guild_result_receipts r WHERE r.QuestId=c.Id AND NOT (
                typeof(r.ResultId)='text' AND length(CAST(r.ResultId AS BLOB))<=36
                AND typeof(r.Digest)='blob' AND length(r.Digest)=32
                AND (r.ReasonCode IS NULL OR (typeof(r.ReasonCode)='text' AND length(CAST(r.ReasonCode AS BLOB)) BETWEEN 1 AND 65536))
                AND (r.MimeType IS NULL OR (typeof(r.MimeType)='text' AND length(CAST(r.MimeType AS BLOB)) BETWEEN 1 AND 65536))
                AND (r.ContentId IS NULL OR (typeof(r.ContentId)='text' AND length(CAST(r.ContentId AS BLOB))<=36))
                AND typeof(r.OutcomeCode)='integer' AND r.OutcomeCode IN(3,4,5,6)
                AND typeof(r.NoDispatchProven)='integer' AND r.NoDispatchProven IN(0,1)
                AND typeof(r.WorkStoppedProven)='integer' AND r.WorkStoppedProven IN(0,1)
                AND ((r.ContentId IS NULL AND r.ContentRevision IS NULL AND r.MimeType IS NULL AND r.ByteLength IS NULL)
                    OR (r.ContentId IS NOT NULL AND typeof(r.ContentRevision)='integer' AND r.ContentRevision>=1
                        AND r.MimeType IS NOT NULL AND typeof(r.ByteLength)='integer' AND r.ByteLength BETWEEN 0 AND 65536))))
            """);

        predicates.Add("NOT EXISTS(SELECT 1 FROM guild_result_outbox b WHERE b.QuestId=c.Id AND (typeof(b.DeliveryStateCode)<>'integer' OR b.DeliveryStateCode NOT IN(1,2,3)))");

        await using SqliteCommand command = Command(connection, transaction,
            "SELECT CASE WHEN " + string.Join(" AND ", predicates.Select(predicate => "(" + predicate + ")"))
                + " THEN 1 ELSE 0 END FROM guild_quests c LEFT JOIN LongRunningOperations o ON o.Id=c.OperationId WHERE c.Id=$id"
                + (expectedOperationId is null ? "" : " AND c.OperationId=$operation"));

        Add(command, "$id", Id(id));

        if (expectedOperationId is { } operationId)
            Add(command, "$operation", operationId.ToString("N"));

        object? valid = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return valid is null ? new Error(ErrorCodes.Guild.NotFound, "The Quest is unavailable.")
            : Equals(valid, 1L) ? Result.Success() : Unavailable;
    }

    internal static async Task<bool> ConvergenceSettledWithinAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            WITH RECURSIVE subtree(Id) AS (
                SELECT Id FROM guild_quests WHERE Id=$id
                UNION
                SELECT child.Id FROM guild_quests child JOIN subtree parent ON child.ParentQuestId=parent.Id)
            """ + " SELECT NOT EXISTS(SELECT 1 FROM guild_quests c WHERE c.Id IN(SELECT Id FROM subtree) AND "
                + GuildQuestRetentionPins.StoppedExecutionUnresolved + ")");

        Add(command, "$id", Id(id));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static string BoundedText(string column, int maximumBytes, bool nullable = false)
    {
        string required = "typeof(" + column + ")='text' AND length(CAST(" + column + " AS BLOB))<=" + maximumBytes.ToString(CultureInfo.InvariantCulture);

        return nullable ? column + " IS NULL OR (" + required + ")" : required;
    }

    private static string FixedDigest(string column) => "typeof(" + column + ")='blob' AND length(" + column + ")=32";

    private static async Task<Result<GuildDataRef>> ResolveContentReferenceAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildDataRef reference, Guid? session, Guid? campaign, Guid source, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 0 FROM guild_content WHERE ContentId=$id AND SessionId IS $session AND CampaignId IS $campaign
              AND ContentKindCode=$kind AND SourceRecordId=$source AND Revision=$revision AND Digest=$digest AND MimeType=$mime AND length(Body)=$bytes
            UNION ALL
            SELECT 1 FROM guild_content_erasure_receipts WHERE ContentId=$id AND SessionId IS $session AND CampaignId IS $campaign
              AND ContentKindCode=$kind AND SourceRecordId=$source AND Revision=$revision AND Digest=$digest AND MimeType=$mime AND ByteLength=$bytes
            """);
        Add(command, "$id", Id(reference.ContentId));
        Add(command, "$session", Id(session));
        Add(command, "$campaign", Id(campaign));
        Add(command, "$kind", (int)reference.Kind);
        Add(command, "$source", Id(source));
        Add(command, "$revision", reference.Revision);
        Add(command, "$digest", Convert.FromHexString(reference.Digest));
        Add(command, "$mime", reference.MimeType);
        Add(command, "$bytes", reference.ByteLength);
        object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long erased ? reference with { ContentErased = erased == 1 } : Unavailable;
    }
}
