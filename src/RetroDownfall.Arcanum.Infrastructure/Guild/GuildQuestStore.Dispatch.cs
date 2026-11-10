using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildQuestDispatchRecord>> PrepareDispatchAsync(GuildQuestDriver driver, Guid dispatchId,
        string invocationDigest, Guid accountingAssociationId, bool isContinuation, CancellationToken ct)
    {
        if (!DriverShape(driver) || dispatchId == Guid.Empty || accountingAssociationId == Guid.Empty || !ReceiptDigestValid(invocationDigest))
            return Task.FromResult(Result<GuildQuestDispatchRecord>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildQuestDispatchRecord>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<(GuildQuestStored Row, LongRunningOperation Operation)> admitted = await RequireDriverWithinAsync(
                    connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                Result<GuildQuestDispatchRecord?> prior = await ReadDispatchWithinAsync(connection, transaction, dispatchId, token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } existing)
                    return existing.QuestId == driver.Execution.QuestId && existing.InvocationDigest == invocationDigest
                        && existing.AccountingAssociationId == accountingAssociationId && existing.IsContinuation == isContinuation
                        ? existing : Conflict;

                GuildQuestStored row = admitted.Value.Row;

                if (row.Quest.GoalRef.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The Quest goal was erased.");

                if (await HasClosedAncestorAsync(connection, transaction, row.Quest.Id, token).ConfigureAwait(false)) return ClosedDispatch;

                await using (SqliteCommand eligible = Command(connection, transaction, isContinuation
                    ? "SELECT EXISTS(SELECT 1 FROM guild_continuations WHERE QuestId=$quest AND ContinuationId=$dispatch) AND NOT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$quest AND EvidenceCode=2)"
                    : "SELECT NOT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$quest)"))
                {
                    Add(eligible, "$quest", Id(row.Quest.Id));
                    Add(eligible, "$dispatch", Id(dispatchId));

                    if (!Equals(1L, await eligible.ExecuteScalarAsync(token).ConfigureAwait(false))) return Conflict;
                }

                DateTimeOffset now = time.GetUtcNow();

                Guid? localChild = null;

                await using SqliteCommand remoteKind = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_remote_executions WHERE QuestId=$quest)");

                Add(remoteKind, "$quest", Id(row.Quest.Id));

                bool remoteExecution = Equals(1L, await remoteKind.ExecuteScalarAsync(token).ConfigureAwait(false));

                if (localExecution is not null && !remoteExecution)
                {
                    Result<GuildLocalExecutionBinding> binding = await GuildLocalExecutionStore.ReadWithinAsync(
                        connection, transaction, row.Quest.Id, token).ConfigureAwait(false);

                    if (binding.IsFailure) return binding.Error;

                    localChild = binding.Value.ApprenticeId;
                }

                await using SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO guild_dispatches(DispatchId,QuestId,InvocationDigest,IsContinuation,AccountingAssociationId,
                        EvidenceCode,LocalChildId,Revision,CreatedAtUtc,UpdatedAtUtc)
                    VALUES($id,$quest,$digest,$continuation,$accounting,1,$child,1,$now,$now)
                    """);

                Add(insert, "$id", Id(dispatchId));
                Add(insert, "$quest", Id(row.Quest.Id));
                Add(insert, "$digest", Convert.FromHexString(invocationDigest));
                Add(insert, "$continuation", isContinuation ? 1 : 0);
                Add(insert, "$accounting", Id(accountingAssociationId));
                Add(insert, "$child", Id(localChild));
                Add(insert, "$now", UtcInstantText.Format(now));

                _ = await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    row.Quest.RootQuestId, row.Quest.Id, GuildEventKind.QuestChanged, dispatchId, 1, null, now, token, GuildPortalEventSource.Dispatch).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                return new GuildQuestDispatchRecord(dispatchId, row.Quest.Id, invocationDigest, accountingAssociationId,
                    isContinuation, GuildDispatchEvidence.Prepared, localChild, null, null, 1);
            }, ct);
    }

    internal Task<Result<GuildQuestDispatchAdmission>> MarkPossiblyDispatchedAsync(GuildQuestDriver driver,
        Guid dispatchId, long expectedDispatchRevision, CancellationToken ct)
    {
        if (!DriverShape(driver) || dispatchId == Guid.Empty)
            return Task.FromResult(Result<GuildQuestDispatchAdmission>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildQuestDispatchAdmission>(driver.Execution.RootQuestId,
            (connection, transaction, signets, token) => MarkPossiblyDispatchedWithinAsync(connection, transaction,
                signets, driver, dispatchId, expectedDispatchRevision, token), ct);
    }

    private async Task<Result<GuildQuestDispatchAdmission>> MarkPossiblyDispatchedWithinAsync(
        SqliteConnection connection, SqliteTransaction transaction, SignetAuthority.QuestTransaction signets,
        GuildQuestDriver driver, Guid dispatchId, long expectedDispatchRevision, CancellationToken token)
    {
                Result<(GuildQuestStored Row, LongRunningOperation Operation)> admitted = await RequireDriverWithinAsync(
                    connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                Result<GuildQuestDispatchRecord?> found = await ReadDispatchWithinAsync(connection, transaction, dispatchId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                if (found.Value is not { } dispatch || dispatch.QuestId != driver.Execution.QuestId) return OwnerUnavailable;

                // A replay observes committed evidence; it never grants another attempt at an external effect.
                if (dispatch.Evidence != GuildDispatchEvidence.Prepared) return new GuildQuestDispatchAdmission(dispatch, false);

                if (dispatch.Revision != expectedDispatchRevision) return RevisionConflict;

                if (await HasClosedAncestorAsync(connection, transaction, dispatch.QuestId, token).ConfigureAwait(false)) return ClosedDispatch;

                if (dispatch.Revision == long.MaxValue || admitted.Value.Row.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                Result exposed = await allocations.ExposeAsync(connection, transaction, dispatch.QuestId,
                    admitted.Value.Row.AllocationAccountId, dispatchId, dispatch.AccountingAssociationId, token).ConfigureAwait(false);

                if (exposed.IsFailure) return exposed.Error;

                if (admitted.Value.Operation.LeaseExpiresAt <= time.GetUtcNow()) return RevisionConflict;

                DateTimeOffset now = time.GetUtcNow();

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_dispatches SET EvidenceCode=2,Revision=Revision+1,UpdatedAtUtc=$now
                    WHERE DispatchId=$id AND Revision=$revision AND EvidenceCode=1;
                    UPDATE guild_quests SET LastNonterminalStateCode=1,WaitReasonCode=NULL,Revision=Revision+1,UpdatedAtUtc=$now
                    WHERE Id=$quest;
                    """);

                Add(update, "$id", Id(dispatchId));
                Add(update, "$revision", expectedDispatchRevision);
                Add(update, "$quest", Id(dispatch.QuestId));
                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 2) return RevisionConflict;

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    admitted.Value.Row.Quest.RootQuestId, dispatch.QuestId, GuildEventKind.QuestChanged,
                    dispatchId, dispatch.Revision + 1, null, now, token, GuildPortalEventSource.Dispatch).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                return new GuildQuestDispatchAdmission(dispatch with { Evidence = GuildDispatchEvidence.PossiblyDispatched, Revision = dispatch.Revision + 1 }, true);
    }

    internal Task<Result<GuildQuestDispatchRecord>> RecordDispatchEvidenceAsync(GuildQuestDriver driver,
        GuildQuestDispatchEvidence evidence, CancellationToken ct)
    {
        if (!DriverShape(driver) || evidence is null || evidence.DispatchId == Guid.Empty
            || evidence.Evidence is not (GuildDispatchEvidence.Confirmed or GuildDispatchEvidence.ResolvedNoDispatch)
            || evidence.LocalChildId == Guid.Empty || !RemoteIdentityValid(evidence.RemoteTaskId) || !RemoteIdentityValid(evidence.RemoteContextId)
            || evidence.LocalChildId is not null && (evidence.RemoteTaskId is not null || evidence.RemoteContextId is not null)
            || evidence.RemoteContextId is not null && evidence.RemoteTaskId is null)
            return Task.FromResult(Result<GuildQuestDispatchRecord>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildQuestDispatchRecord>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<(GuildQuestStored Row, LongRunningOperation Operation)> admitted = await RequireDriverWithinAsync(
                    connection, transaction, signets, driver, true, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                Result<GuildQuestDispatchRecord?> found = await ReadDispatchWithinAsync(connection, transaction, evidence.DispatchId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                if (found.Value is not { } dispatch || dispatch.QuestId != driver.Execution.QuestId) return OwnerUnavailable;

                if (dispatch.LocalChildId is not null && evidence.LocalChildId != dispatch.LocalChildId
                    || dispatch.RemoteTaskId is not null && evidence.RemoteTaskId != dispatch.RemoteTaskId
                    || dispatch.RemoteContextId is not null && evidence.RemoteContextId != dispatch.RemoteContextId) return Conflict;

                if (dispatch.Evidence == evidence.Evidence && dispatch.LocalChildId == evidence.LocalChildId
                    && dispatch.RemoteTaskId == evidence.RemoteTaskId && dispatch.RemoteContextId == evidence.RemoteContextId) return dispatch;

                if (dispatch.Revision != evidence.ExpectedRevision) return RevisionConflict;

                if (evidence.Evidence == GuildDispatchEvidence.Confirmed
                    ? dispatch.Evidence is not (GuildDispatchEvidence.PossiblyDispatched or GuildDispatchEvidence.Confirmed) || evidence.LocalChildId is null && evidence.RemoteTaskId is null
                    : !evidence.NoDispatchProven || dispatch.Evidence is not (GuildDispatchEvidence.Prepared or GuildDispatchEvidence.PossiblyDispatched)
                        || evidence.LocalChildId != dispatch.LocalChildId || evidence.RemoteTaskId is not null || evidence.RemoteContextId is not null)
                    return UncertainDispatch;

                if (dispatch.Revision == long.MaxValue) return ExhaustedReceipt;

                DateTimeOffset now = time.GetUtcNow();

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_dispatches SET EvidenceCode=$evidence,LocalChildId=$child,RemoteTaskId=$task,RemoteContextId=$context,
                        Revision=Revision+1,UpdatedAtUtc=$now WHERE DispatchId=$id AND Revision=$revision
                    """);

                Add(update, "$id", Id(evidence.DispatchId));
                Add(update, "$revision", evidence.ExpectedRevision);
                Add(update, "$evidence", (int)evidence.Evidence);
                Add(update, "$child", Id(evidence.LocalChildId));
                Add(update, "$task", evidence.RemoteTaskId);
                Add(update, "$context", evidence.RemoteContextId);
                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
                    admitted.Value.Row.Quest.RootQuestId, dispatch.QuestId, GuildEventKind.QuestChanged,
                    dispatch.DispatchId, dispatch.Revision + 1, null, now, token, GuildPortalEventSource.Dispatch).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                return dispatch with { Evidence = evidence.Evidence, LocalChildId = evidence.LocalChildId, RemoteTaskId = evidence.RemoteTaskId,
                    RemoteContextId = evidence.RemoteContextId, Revision = dispatch.Revision + 1 };
            }, ct);
    }

    internal async Task<Result<(GuildQuestStored Row, LongRunningOperation Operation)>> RequireDriverWithinAsync(
        SqliteConnection connection, SqliteTransaction transaction, SignetAuthority.QuestTransaction signets,
        GuildQuestDriver driver, bool requireLease, bool allowCancelling, CancellationToken ct)
    {
        bool cleanup = driver.Caller?.Kind == GuildCallerKind.HostReceipt;

        if (cleanup && !allowCancelling) return ClosedDispatch;

        if (!cleanup)
        {
            Result<GuildGrant> live = await signets.VerifyAsync(driver.Execution, ct).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;
        }

        Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, driver.Execution.QuestId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildQuestStored row = found.Value;

        Result<GuildSignetRecord> signet = await GuildSignetStore.ReadAsync(connection, transaction, row.Quest.SignetId, false, ct).ConfigureAwait(false);

        if (signet.IsFailure) return signet.Error;

        GuildCallerContext caller = driver.Caller ?? new(GuildCallerKind.Run, signet.Value.Binding.OriginPrincipalId, driver.Execution.RunId,
            policy.InstallationGeneration, driver.Execution);

        if (cleanup)
        {
            if (policy is not GuildLocalHostPolicy host) return Unavailable;

            Result permitted = await host.AuthorizeRemoteCleanupWithinAsync(driver, row, connection, transaction, ct).ConfigureAwait(false);

            if (permitted.IsFailure) return permitted.Error;
        }
        else
        {
            if (caller.Kind != GuildCallerKind.Run || !ReferenceEquals(caller.Execution, driver.Execution)
                || caller.PrincipalId != signet.Value.Binding.OriginPrincipalId || caller.InstallationGeneration != policy.InstallationGeneration)
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "The driver does not bind its authenticated Run caller.");

            Result visible = await RequireVisibleAsync(connection, transaction, caller, row, ct).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;
        }

        if (driver.Execution.SignetId != row.Quest.SignetId || driver.Execution.AgentId != row.Quest.AgentId
            || driver.Execution.RunId != row.Quest.RunId || driver.Execution.RootQuestId != row.Quest.RootQuestId)
            return new Error(ErrorCodes.Guild.AuthorityMismatch, "The driver does not bind this Quest.");

        if (requireLease && row.Quest.ReconciliationRequired) return UncertainDispatch;

        if (requireLease && row.Quest.ResultReceiptId is not null) return ClosedDispatch;

        if (requireLease && !allowCancelling
            && await HasClosedAncestorAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false)) return ClosedDispatch;

        LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, row.Quest.OperationId, ct).ConfigureAwait(false);

        if (operation is null || operation.Kind != LongRunningOperationKinds.GuildQuest || operation.RunId != driver.Execution.RunId
            || operation.SessionId != row.Quest.SessionId) return Unavailable;

        Result<GuildQuestCheckpointPayload> checkpoint = GuildQuestCheckpoint.Decode(operation.CheckpointVersion, operation.CheckpointPayload);

        if (checkpoint.IsFailure || checkpoint.Value.QuestId != row.Quest.Id) return UncertainDispatch;

        if (requireLease && (operation.Revision != driver.OperationRevision || operation.LeaseOwner != driver.LeaseOwner
            || operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= time.GetUtcNow()
            || operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting)
                && !(allowCancelling && operation.State == LongRunningOperationState.Cancelling))) return RevisionConflict;

        return (row, operation);
    }

    private static async Task<Result<GuildQuestDispatchRecord?>> ReadDispatchWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid id, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT DispatchId,QuestId,InvocationDigest,AccountingAssociationId,IsContinuation,EvidenceCode,LocalChildId,
                RemoteTaskId,RemoteContextId,Revision,
                CASE WHEN length(CAST(DispatchId AS BLOB))=36 AND length(CAST(QuestId AS BLOB))=36
                    AND typeof(InvocationDigest)='blob' AND length(InvocationDigest)=32
                    AND length(CAST(AccountingAssociationId AS BLOB))=36 AND IsContinuation IN(0,1) AND EvidenceCode IN(1,2,3,4)
                    AND (LocalChildId IS NULL OR length(CAST(LocalChildId AS BLOB))=36)
                    AND (RemoteTaskId IS NULL OR length(CAST(RemoteTaskId AS BLOB)) BETWEEN 1 AND 65536)
                    AND (RemoteContextId IS NULL OR length(CAST(RemoteContextId AS BLOB)) BETWEEN 1 AND 65536)
                    AND typeof(Revision)='integer' AND Revision>=1 THEN 1 ELSE 0 END
            FROM guild_dispatches WHERE DispatchId=$id
            """);

        Add(command, "$id", Id(id));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildQuestDispatchRecord?>.Success(null);

        if (reader.GetInt64(10) != 1) return Unavailable;

        return Result<GuildQuestDispatchRecord?>.Success(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            Convert.ToHexString((byte[])reader.GetValue(2)), Guid.Parse(reader.GetString(3)), reader.GetBoolean(4), (GuildDispatchEvidence)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9)));
    }

    private static bool DriverShape(GuildQuestDriver driver) => driver?.Execution is not null && !string.IsNullOrWhiteSpace(driver.LeaseOwner);

    private static bool ReceiptDigestValid(string? digest) => digest is { Length: 64 }
        && digest.All(value => value is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool RemoteIdentityValid(string? identity) => identity is null || Encoding.UTF8.GetByteCount(identity) is > 0 and <= GuildStore.MaximumContentBytes;

    private static Error InvalidReceipt => new(ErrorCodes.Guild.InvalidRequest, "The Quest receipt or dispatch identity is invalid.");

    private static Error ExhaustedReceipt => new(ErrorCodes.Guild.SequenceExhausted, "The Quest evidence revision is exhausted.");

    private static Error ClosedDispatch => new(ErrorCodes.Guild.Forbidden, "The Quest lineage is closed to new execution.");

    private static Error UncertainDispatch => new(ErrorCodes.Guild.ReconciliationRequired, "The Quest has unresolved execution evidence.");
}
