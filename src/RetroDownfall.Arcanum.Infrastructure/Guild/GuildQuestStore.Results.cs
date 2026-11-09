using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildResultReceipt>> CommitResultAsync(GuildQuestDriver driver, GuildMutationIdentity mutation,
        long expectedQuestRevision, GuildQuestResult result, CancellationToken ct)
    {
        if (!DriverShape(driver) || !ReceiptMutationValid(mutation) || !ResultShapeValid(result)
            || result.Receipt.QuestId != driver.Execution.QuestId)
            return Task.FromResult(Result<GuildResultReceipt>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildResultReceipt>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                // A committed result is adopted under current authority before applying a new lease/revision CAS.
                Result<(GuildQuestStored Row, LongRunningOperation Operation)> visible = await RequireDriverWithinAsync(
                    connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                GuildQuestStored row = visible.Value.Row;

                Result<GuildResultReceipt?> prior = await ReadResultWithinAsync(connection, transaction, row, token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } accepted)
                    return SameTerminalResult(accepted, result.Receipt) && !row.Quest.ReconciliationRequired ? accepted : Conflict;

                Result<(GuildQuestStored Row, LongRunningOperation Operation)> admitted = await RequireDriverWithinAsync(
                    connection, transaction, signets, driver, true, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (row.Quest.Revision != expectedQuestRevision) return RevisionConflict;

                if (row.Quest.GoalRef.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The Quest goal was erased.");

                if (row.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                Result proof = await RequireOutcomeProofWithinAsync(connection, transaction, row, admitted.Value.Operation, result, token).ConfigureAwait(false);

                if (proof.IsFailure) return proof.Error;

                Result run = await FinishProviderRunWithinAsync(connection, transaction, row,
                    result.Receipt.Outcome == QuestState.Completed ? InferenceRunStatus.Completed
                        : result.Receipt.Outcome == QuestState.Canceled ? InferenceRunStatus.Abandoned : InferenceRunStatus.Failed,
                    token).ConfigureAwait(false);

                if (run.IsFailure) return run.Error;

                GuildResultReceipt receipt = result.Receipt;

                DateTimeOffset now = time.GetUtcNow();

                if (receipt.Content is { } content)
                {
                    Result<DerivedArtifactWrite> sensitivity = await ResultSensitivityWithinAsync(connection, transaction, row,
                        content.ContentId, result.Body!, result.SourceLabel, token).ConfigureAwait(false);

                    if (sensitivity.IsFailure) return sensitivity.Error;

                    Result<GuildDataRef> written = await GuildStore.WriteContentWithinAsync(connection, transaction,
                        new(content.ContentId, receipt.ResultId, GuildContentKind.QuestResult, 1, row.Quest.SessionId,
                            row.Quest.CampaignId, content.MimeType, result.Body!, sensitivity.Value), now, token).ConfigureAwait(false);

                    if (written.IsFailure) return written.Error;

                    receipt = receipt with { Content = written.Value };
                }

                await using (SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,ContentId,ContentRevision,MimeType,ByteLength,
                        OutcomeCode,ReasonCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                    VALUES($quest,$result,$digest,$content,$contentRevision,$mime,$bytes,$outcome,$reason,$noDispatch,$stopped,$mutation,$revision,$now)
                    """))
                {
                    BindResult(insert, receipt);
                    Add(insert, "$content", Id(receipt.Content?.ContentId));
                    Add(insert, "$contentRevision", receipt.Content?.Revision);
                    Add(insert, "$mime", receipt.Content?.MimeType);
                    Add(insert, "$bytes", receipt.Content?.ByteLength);
                    Add(insert, "$outcome", (int)receipt.Outcome);
                    Add(insert, "$reason", receipt.ReasonCode);
                    Add(insert, "$noDispatch", result.NoDispatchProven ? 1 : 0);
                    Add(insert, "$stopped", result.WorkStoppedProven ? 1 : 0);
                    Add(insert, "$mutation", Id(mutation.IdempotencyKey));
                    Add(insert, "$revision", row.Quest.Revision + 1);
                    Add(insert, "$now", UtcInstantText.Format(now));

                    _ = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                LongRunningOperationState terminal = receipt.Outcome is QuestState.Completed or QuestState.Canceled
                    ? LongRunningOperationState.Completed : LongRunningOperationState.Failed;

                if (admitted.Value.Operation.LeaseExpiresAt <= time.GetUtcNow()
                    || !await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, row.Quest.OperationId,
                        driver.OperationRevision, driver.LeaseOwner, terminal, now, receipt.ReasonCode, token).ConfigureAwait(false)) return RevisionConflict;

                await using (SqliteCommand outbox = Command(connection, transaction, """
                    INSERT INTO guild_result_outbox(QuestId,ResultId,RecipientQuestId,Digest,DeliveryStateCode,Revision,CreatedAtUtc)
                    VALUES($quest,$result,$recipient,$digest,1,1,$now)
                    """))
                {
                    BindResult(outbox, receipt);
                    Add(outbox, "$recipient", Id(row.Quest.ParentQuestId));
                    Add(outbox, "$now", UtcInstantText.Format(now));

                    _ = await outbox.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                Result<GuildAccountingState> settled = await allocations.SettleAsync(connection, transaction, row.Quest.Id,
                    row.AllocationAccountId, receipt, token).ConfigureAwait(false);

                if (settled.IsFailure) return settled.Error;

                if (!Enum.IsDefined(settled.Value)) return Unavailable;

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_quests SET AccountingStateCode=$accounting,CancellationConverged=$converged,
                        CancellationRequested=CASE WHEN $failed=1 THEN 1 ELSE CancellationRequested END,
                        Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision
                    """);

                Add(update, "$quest", Id(row.Quest.Id));
                Add(update, "$revision", expectedQuestRevision);
                Add(update, "$accounting", (int)settled.Value);
                Add(update, "$converged", receipt.Outcome == QuestState.Canceled ? 1 : 0);
                Add(update, "$failed", receipt.Outcome == QuestState.Failed ? 1 : 0);
                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                Result disposition = await GuildPortalCleanupParticipant.WithholdTerminalWithinAsync(
                    connection, transaction, row.Quest.Id, now, token).ConfigureAwait(false);

                if (disposition.IsFailure) return disposition.Error;

                Result<long> projected = await GuildPortalEvents.RecordSourceWithinAsync(connection, transaction,
                    row.Quest.RootQuestId, row.Quest.Id, GuildReceiptSourceKind.Result, receipt.ResultId,
                    expectedQuestRevision + 1, now, token).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                if (settled.Value == GuildAccountingState.Settled)
                {
                    Result<long> settlement = await GuildPortalEvents.RecordSourceWithinAsync(connection, transaction,
                        row.Quest.RootQuestId, row.Quest.Id, GuildReceiptSourceKind.Settlement, receipt.ResultId,
                        expectedQuestRevision + 1, now, token).ConfigureAwait(false);

                    if (settlement.IsFailure) return settlement.Error;
                }

                return receipt;
            }, ct);
    }

    internal async Task<Result<GuildResultApplicationReceipt>> ApplyResultAsync(GuildCallerContext caller,
        GuildResultApplicationRequest request, CancellationToken ct)
    {
        if (caller is null || request is null || !ReceiptMutationValid(request.Mutation) || request.RecipientQuestId == Guid.Empty
            || request.QuestId == Guid.Empty || request.ResultId == Guid.Empty || !ReceiptDigestValid(request.CanonicalContentDigest)
            || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run)
            || caller.Kind == GuildCallerKind.Run && caller.Execution?.QuestId != request.RecipientQuestId)
            return InvalidReceipt;

        // Root lineage is immutable; resolve its exact gate before the one application write transaction.
        Result<GuildQuestStored> located = await GetAsync(caller, request.RecipientQuestId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildResultApplicationReceipt>(located.Value.Quest.RootQuestId,
            (connection, transaction, signets, token) => ApplyResultWithinAsync(connection, transaction, signets, caller, request, token), ct).ConfigureAwait(false);
    }

    internal async Task<Result<GuildResultApplicationReceipt>> ApplyResultWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, SignetAuthority.QuestTransaction signets, GuildCallerContext caller,
        GuildResultApplicationRequest request, CancellationToken token)
    {
        Result<GuildQuestStored> recipient = await ReadWithinAsync(connection, transaction, request.RecipientQuestId, token).ConfigureAwait(false);

        if (recipient.IsFailure) return recipient.Error;

        Result recipientVisible = await RequireVisibleAsync(connection, transaction, caller, recipient.Value, token).ConfigureAwait(false);

        if (recipientVisible.IsFailure) return recipientVisible.Error;

        if (caller.Execution is { } execution)
        {
            Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;
        }

        Result<GuildQuestStored> source = await ReadWithinAsync(connection, transaction, request.QuestId, token).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        Result sourceVisible = await RequireVisibleAsync(connection, transaction, caller, source.Value, token).ConfigureAwait(false);

        if (sourceVisible.IsFailure) return sourceVisible.Error;

        if (source.Value.Quest.ParentQuestId != recipient.Value.Quest.Id
            || source.Value.Quest.RootQuestId != recipient.Value.Quest.RootQuestId) return OwnerUnavailable;

        Result<GuildResultReceipt?> recorded = await ReadResultWithinAsync(connection, transaction, source.Value, token).ConfigureAwait(false);

        if (recorded.IsFailure) return recorded.Error;

        if (recorded.Value is not { } result) return Unavailable;

        if (result.ResultId != request.ResultId || result.CanonicalContentDigest != request.CanonicalContentDigest) return Conflict;

        if (source.Value.Quest.ReconciliationRequired) return UncertainDispatch;

        Result<GuildResultApplicationReceipt?> prior = await ReadApplicationWithinAsync(connection, transaction, request, token).ConfigureAwait(false);

        if (prior.IsFailure) return prior.Error;

        if (prior.Value is { } accepted) return accepted with { Receipt = accepted.Receipt with { Replayed = true } };

        Result<GuildGrant> eligible = await signets.ValidateCurrentGrantAsync(recipient.Value.Quest.Id, token).ConfigureAwait(false);

        if (eligible.IsFailure) return eligible.Error;

        if (recipient.Value.Quest.Revision != request.ExpectedRecipientRevision) return RevisionConflict;

        if (recipient.Value.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

        if (result.Content?.ContentErased == true) return new Error(ErrorCodes.Guild.ContentErased, "The result body was erased before application.");

        if (recipient.Value.Quest.State is QuestState.Completed or QuestState.Failed or QuestState.Rejected or QuestState.Canceled
            || recipient.Value.Quest.CancellationRequested) return ClosedDispatch;

        DateTimeOffset now = time.GetUtcNow();

        long revision = recipient.Value.Quest.Revision + 1;

        await using (SqliteCommand apply = Command(connection, transaction, """
            INSERT INTO guild_result_applications(RecipientQuestId,QuestId,ResultId,Digest,MutationId,AcceptedRecipientRevision,CreatedAtUtc)
            VALUES($recipient,$quest,$result,$digest,$mutation,$revision,$now);
            INSERT INTO guild_result_inputs(RecipientQuestId,QuestId,ResultId,Digest,CreatedAtUtc)
            VALUES($recipient,$quest,$result,$digest,$now);
            """))
        {
            BindResult(apply, result);
            Add(apply, "$recipient", Id(request.RecipientQuestId));
            Add(apply, "$mutation", Id(request.Mutation.IdempotencyKey));
            Add(apply, "$revision", revision);
            Add(apply, "$now", UtcInstantText.Format(now));

            if (await apply.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2) return Unavailable;
        }

        await using (SqliteCommand ack = Command(connection, transaction, """
            UPDATE guild_result_outbox SET DeliveryStateCode=2,Revision=Revision+1,AcknowledgedAtUtc=$now
            WHERE QuestId=$quest AND ResultId=$result AND Digest=$digest AND RecipientQuestId=$recipient
                AND DeliveryStateCode=1 AND Revision<9223372036854775807;
            """))
        {
            BindResult(ack, result);
            Add(ack, "$recipient", Id(request.RecipientQuestId));
            Add(ack, "$now", UtcInstantText.Format(now));

            if (await ack.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Unavailable;
        }

        await using SqliteCommand update = Command(connection, transaction, """
            UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$recipient AND Revision=$expected
            """);

        Add(update, "$recipient", Id(request.RecipientQuestId));
        Add(update, "$expected", request.ExpectedRecipientRevision);
        Add(update, "$now", UtcInstantText.Format(now));

        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
            recipient.Value.Quest.RootQuestId, recipient.Value.Quest.Id, GuildEventKind.QuestChanged,
            recipient.Value.Quest.Id, revision, null, now, token).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        return new GuildResultApplicationReceipt(request.RecipientQuestId, result.QuestId, result.ResultId,
            result.CanonicalContentDigest, revision, new(result.ResultId, request.Mutation.IdempotencyKey, false, now, revision));
    }

    private static async Task<Result> RequireOutcomeProofWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored row, LongRunningOperation operation, GuildQuestResult result, CancellationToken ct)
    {
        long attempts, confirmed;

        await using (SqliteCommand evidence = Command(connection, transaction, """
            SELECT COALESCE(SUM(CASE WHEN EvidenceCode IN(2,3) OR RemoteTaskId IS NOT NULL OR RemoteContextId IS NOT NULL THEN 1 ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN EvidenceCode=3 THEN 1 ELSE 0 END),0)
            FROM guild_dispatches WHERE QuestId=$id
            """))
        {
            Add(evidence, "$id", Id(row.Quest.Id));

            await using SqliteDataReader reader = await evidence.ExecuteReaderAsync(ct).ConfigureAwait(false);

            _ = await reader.ReadAsync(ct).ConfigureAwait(false);

            attempts = reader.GetInt64(0);
            confirmed = reader.GetInt64(1);
        }

        if (result.NoDispatchProven && attempts != 0) return UncertainDispatch;

        QuestState outcome = result.Receipt.Outcome;

        if (outcome == QuestState.Completed && (attempts != confirmed || confirmed == 0 && !result.NoDispatchProven)) return UncertainDispatch;

        if (outcome == QuestState.Rejected && (!result.NoDispatchProven || attempts != 0)) return UncertainDispatch;

        if (outcome == QuestState.Canceled && (!result.WorkStoppedProven
            || !row.Quest.CancellationRequested && operation.State != LongRunningOperationState.Cancelling)) return UncertainDispatch;

        if (result.WorkStoppedProven || outcome is QuestState.Rejected or QuestState.Canceled)
        {
            await using SqliteCommand unresolved = Command(connection, transaction, $"""
                WITH RECURSIVE tree(Id,OperationId) AS (
                    SELECT Id,OperationId FROM guild_quests WHERE Id=$id
                    UNION SELECT c.Id,c.OperationId FROM guild_quests c JOIN tree p ON c.ParentQuestId=p.Id)
                SELECT ($subtree=1 AND EXISTS(SELECT 1 FROM tree t JOIN guild_quests c ON c.Id=t.Id
                        WHERE c.Id<>$id AND {GuildQuestRetentionPins.StoppedExecutionUnresolved}))
                    OR EXISTS(SELECT 1 FROM tree t JOIN guild_quests c ON c.Id=t.Id
                        WHERE (c.Id=$id OR $subtree=1) AND {GuildQuestRetentionPins.PhysicalWorkUnresolved})
                    OR EXISTS(SELECT 1 FROM guild_budget_effects effect WHERE effect.QuestId=$id
                        AND (effect.StateCode=1 OR (effect.StateCode=0 AND $noDispatch=0)))
                """);

            Add(unresolved, "$id", Id(row.Quest.Id));

            // Failure records this execution's stopped outcome immediately and leaves its
            // descendants behind the durable frontier; cancellation requires the whole subtree.
            Add(unresolved, "$subtree", outcome == QuestState.Failed ? 0 : 1);

            // The result transaction settles its own prepared, never-sent holds atomically.
            // Possible dispatch still requires explicit effect completion evidence.
            Add(unresolved, "$noDispatch", result.NoDispatchProven ? 1 : 0);

            if (Equals(1L, await unresolved.ExecuteScalarAsync(ct).ConfigureAwait(false))) return UncertainDispatch;
        }

        return Result.Success();
    }

    private static async Task<Result<DerivedArtifactWrite>> ResultSensitivityWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored row, Guid contentId, byte[] body, ArtifactSensitivityLabel? source, CancellationToken ct)
    {
        Result<ArtifactSensitivityLabel?> goal = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
            SensitiveArtifactKind.GuildContent, row.Quest.GoalRef.ContentId, ct).ConfigureAwait(false);

        if (goal.IsFailure) return goal.Error;

        GenerationProvenance provenance = (goal.Value?.Provenance ?? GenerationProvenance.CreateExact([]))
            .Merge(source?.Provenance ?? GenerationProvenance.CreateExact([]));

        ContentSensitivity sensitivity = goal.Value?.Sensitivity == ContentSensitivity.CovenantDerived || source?.Sensitivity == ContentSensitivity.CovenantDerived
            ? ContentSensitivity.CovenantDerived : ContentSensitivity.None;

        ArtifactSensitivityLabel? origin = source ?? goal.Value;

        return new DerivedArtifactWrite(SensitiveArtifactKind.GuildContent, contentId, row.Quest.SessionId, row.Quest.CampaignId,
            origin?.TurnId, 1, new CovenantDigest(SHA256.HashData(body)), sensitivity, provenance,
            origin?.ProducingPlanDigest, origin?.ProducingAdmissionDigest, origin?.ProducingMaintenanceReceiptDigest);
    }

    private static async Task<Result<GuildResultReceipt?>> ReadResultWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored row, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT ResultId,Digest,OutcomeCode,ReasonCode,
                CASE WHEN length(CAST(ResultId AS BLOB))=36 AND typeof(Digest)='blob' AND length(Digest)=32
                    AND OutcomeCode IN(3,4,5,6) AND (ReasonCode IS NULL OR length(CAST(ReasonCode AS BLOB)) BETWEEN 1 AND 128) THEN 1 ELSE 0 END
            FROM guild_result_receipts WHERE QuestId=$id
            """);

        Add(command, "$id", Id(row.Quest.Id));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildResultReceipt?>.Success(null);

        if (reader.GetInt64(4) != 1) return Unavailable;

        return Result<GuildResultReceipt?>.Success(new(row.Quest.Id, Guid.Parse(reader.GetString(0)),
            Convert.ToHexString((byte[])reader.GetValue(1)), row.Quest.ResultRef, (QuestState)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3)));
    }

    private static async Task<Result<GuildResultApplicationReceipt?>> ReadApplicationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildResultApplicationRequest request, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT a.ResultId,a.Digest,a.MutationId,a.AcceptedRecipientRevision,a.CreatedAtUtc,
                EXISTS(SELECT 1 FROM guild_result_inputs i WHERE i.RecipientQuestId=a.RecipientQuestId AND i.QuestId=a.QuestId
                    AND i.ResultId=a.ResultId AND i.Digest=a.Digest)
                AND EXISTS(SELECT 1 FROM guild_result_outbox o WHERE o.QuestId=a.QuestId AND o.RecipientQuestId=a.RecipientQuestId
                    AND o.ResultId=a.ResultId AND o.Digest=a.Digest AND o.DeliveryStateCode=2 AND o.AcknowledgedAtUtc IS NOT NULL)
            FROM guild_result_applications a WHERE a.RecipientQuestId=$recipient AND a.QuestId=$quest
            """);

        Add(command, "$recipient", Id(request.RecipientQuestId));
        Add(command, "$quest", Id(request.QuestId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildResultApplicationReceipt?>.Success(null);

        if (reader.GetInt64(5) != 1) return Unavailable;

        Guid result = Guid.Parse(reader.GetString(0));

        string digest = Convert.ToHexString((byte[])reader.GetValue(1));

        if (result != request.ResultId || digest != request.CanonicalContentDigest) return Conflict;

        long revision = reader.GetInt64(3);

        return Result<GuildResultApplicationReceipt?>.Success(new(request.RecipientQuestId, request.QuestId, result, digest,
            revision, new(result, Guid.Parse(reader.GetString(2)), false, UtcInstantText.Parse(reader.GetString(4)), revision)));
    }

    private static bool ReceiptMutationValid(GuildMutationIdentity? mutation) => mutation is not null
        && mutation.IdempotencyKey != Guid.Empty && ReceiptDigestValid(mutation.CanonicalRequestDigest);

    private static bool ResultShapeValid(GuildQuestResult? result)
    {
        if (result?.Receipt is not { } receipt || receipt.QuestId == Guid.Empty || receipt.ResultId == Guid.Empty
            || !ReceiptDigestValid(receipt.CanonicalContentDigest) || result.Body?.Length > GuildStore.MaximumContentBytes
            || receipt.CanonicalContentDigest != Convert.ToHexString(SHA256.HashData(result.Body ?? []))) return false;

        if (receipt.Content is { } content)
        {
            if (result.Body is null || content.ContentId == Guid.Empty || content.Kind != GuildContentKind.QuestResult || content.Revision != 1
                || content.Digest != receipt.CanonicalContentDigest || content.ByteLength != result.Body.Length || content.ContentErased
                || content.MimeType is not ("text/plain" or "application/json")) return false;
        }
        else if (result.Body is not null) return false;

        return receipt.Outcome switch
        {
            QuestState.Completed => receipt.ReasonCode is null && receipt.Content is not null,
            QuestState.Rejected => receipt.ReasonCode == ErrorCodes.Guild.Rejected,
            QuestState.Canceled => receipt.ReasonCode == LongRunningOperationRecoveryOutcomes.Cancelled,
            QuestState.Failed => receipt.ReasonCode is { Length: > 0 and <= 128 }
                && receipt.ReasonCode is not ErrorCodes.Guild.Rejected and not LongRunningOperationRecoveryOutcomes.Cancelled and not ErrorCodes.Guild.ReconciliationRequired
                && receipt.ReasonCode.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-'),
            _ => false
        };
    }

    private static bool SameTerminalResult(GuildResultReceipt left, GuildResultReceipt right) => left.QuestId == right.QuestId
        && left.ResultId == right.ResultId && left.CanonicalContentDigest == right.CanonicalContentDigest
        && left.Outcome == right.Outcome && left.ReasonCode == right.ReasonCode;

    private static void BindResult(SqliteCommand command, GuildResultReceipt receipt)
    {
        Add(command, "$quest", Id(receipt.QuestId));
        Add(command, "$result", Id(receipt.ResultId));
        Add(command, "$digest", Convert.FromHexString(receipt.CanonicalContentDigest));
    }
}
