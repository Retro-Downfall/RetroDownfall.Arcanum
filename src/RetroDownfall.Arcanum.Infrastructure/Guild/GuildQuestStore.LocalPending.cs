using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildLocalPendingWork(GuildQuestStored Quest, LongRunningOperation Operation,
    GuildDispatchRequest? Request, bool RequiresAttention);

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildLocalPendingWork>> ReadLocalPendingAsync(GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || questId == Guid.Empty) return InvalidReceipt;

        var visible = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        return await authority.InQuestTransactionAsync<GuildLocalPendingWork>(visible.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result allowed = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                return await ReadLocalPendingWithinAsync(connection, transaction, current.Value, time.GetUtcNow(), token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    internal Task<Result<GuildDispatchRequest>> PrepareLocalScheduleAsync(GuildCallerContext caller,
        GuildExecutionHandle execution, long expectedOperationRevision, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || execution is null || expectedOperationRevision < 0)
            return Task.FromResult(Result<GuildDispatchRequest>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildDispatchRequest>(execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (policy is GuildLocalHostPolicy { IsExecutionEnabled: false }) return Unavailable;

                var live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                var current = await ReadWithinAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result allowed = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                DateTimeOffset now = time.GetUtcNow();

                var pending = await ReadLocalPendingWithinAsync(connection, transaction, current.Value, now, token).ConfigureAwait(false);

                if (pending.IsFailure) return pending.Error;

                if (pending.Value.Operation.Revision != expectedOperationRevision) return RevisionConflict;

                if (pending.Value.RequiresAttention || pending.Value.Request is not { } request) return UncertainDispatch;

                if (pending.Value.Operation.State != LongRunningOperationState.ReconciliationRequired) return request;

                // Only a generic crash park at a proven frontier is reopened. Attempts and accepted
                // request/child identities stay intact; explicit runtime interruption remains parked.
                if (pending.Value.Operation.TerminalErrorCode != ErrorCodes.Guild.ReconciliationRequired) return UncertainDispatch;

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, request.OperationId,
                    expectedOperationRevision, null, LongRunningOperationState.Pending, now, null, token).ConfigureAwait(false)) return RevisionConflict;

                await using SqliteCommand update = Command(connection, transaction, """
                    UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision AND Revision<9223372036854775807
                    """);

                Add(update, "$quest", Id(execution.QuestId));

                Add(update, "$revision", current.Value.Quest.Revision);

                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, execution.RootQuestId,
                    execution.QuestId, GuildEventKind.QuestChanged, execution.QuestId, current.Value.Quest.Revision + 1,
                    null, now, token).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : request with { ExpectedOperationRevision = expectedOperationRevision + 1 };
            }, ct);
    }

    private static async Task<Result<GuildLocalPendingWork>> ReadLocalPendingWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored row, DateTimeOffset now, CancellationToken ct)
    {
        LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, row.Quest.OperationId, ct).ConfigureAwait(false);

        if (operation is null || operation.Kind != LongRunningOperationKinds.GuildQuest) return Unavailable;

        GuildLocalPendingWork parked = new(row, operation, null, true);

        if (row.Quest.ResultReceiptId is not null) return parked with { RequiresAttention = false };

        if (row.Retired || row.Quest.CancellationRequested || operation.State is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned or LongRunningOperationState.Cancelling)
            return parked;

        var binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        if (binding.Value.Input.ContentErased || row.Quest.GoalRef.ContentErased) return parked;

        await using (SqliteCommand intent = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_quest_start_intents WHERE QuestId=$quest)"))
        {
            Add(intent, "$quest", Id(row.Quest.Id));

            if (!Equals(1L, await intent.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Unavailable;
        }

        if ((await RequireWaitFrontierWithinAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false)).IsFailure)
            return parked;

        if (await HasClosedAncestorAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false)) return parked;

        if (row.Quest.State == QuestState.InputRequired && row.Quest.WaitReason == GuildWaitReason.Input)
            return parked with { RequiresAttention = false };

        if (operation.State == LongRunningOperationState.ReconciliationRequired
            && operation.TerminalErrorCode != ErrorCodes.Guild.ReconciliationRequired) return parked;

        Guid dispatchId = row.Quest.Id;

        GuildMutationIdentity mutation = new(row.MutationId, row.IntentDigest);

        string invocationDigest = binding.Value.Input.Digest;

        bool continuation = false;

        await using (SqliteCommand latest = Command(connection, transaction, """
            SELECT ContinuationId,MutationId,Digest FROM guild_continuations
            WHERE QuestId=$quest ORDER BY AcceptedRevision DESC LIMIT 1
            """))
        {
            Add(latest, "$quest", Id(row.Quest.Id));

            await using SqliteDataReader reader = await latest.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                continuation = true;

                dispatchId = Guid.Parse(reader.GetString(0));

                mutation = new(Guid.Parse(reader.GetString(1)), Convert.ToHexString((byte[])reader.GetValue(2)));
            }
        }

        if (continuation)
        {
            var answer = await ReadContinuationWithinAsync(connection, transaction, row, dispatchId, ct).ConfigureAwait(false);

            if (answer.IsFailure || answer.Value.ContentRef.ContentErased) return parked;

            invocationDigest = answer.Value.ContentRef.Digest;

            await using SqliteCommand checkpoint = Command(connection, transaction, $"""
                SELECT EXISTS(SELECT 1 FROM guild_local_wait_checkpoints w JOIN guild_content content ON content.ContentId=w.ContentId
                    WHERE w.QuestId=$quest AND w.ContinuationId=$continuation AND {WaitCheckpointProof}
                      AND NOT EXISTS(SELECT 1 FROM guild_content_erasure_receipts erased WHERE erased.ContentId=w.ContentId))
                """);

            Add(checkpoint, "$quest", Id(row.Quest.Id));

            Add(checkpoint, "$continuation", Id(dispatchId));

            if (!Equals(1L, await checkpoint.ExecuteScalarAsync(ct).ConfigureAwait(false))) return parked;
        }
        else
        {
            await using SqliteCommand prior = Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$quest AND DispatchId<>$dispatch)
                    OR EXISTS(SELECT 1 FROM guild_budget_effects WHERE QuestId=$quest)
                """);

            Add(prior, "$quest", Id(row.Quest.Id));

            Add(prior, "$dispatch", Id(dispatchId));

            if (!Equals(0L, await prior.ExecuteScalarAsync(ct).ConfigureAwait(false))) return parked;
        }

        var dispatch = await ReadDispatchWithinAsync(connection, transaction, dispatchId, ct).ConfigureAwait(false);

        if (dispatch.IsFailure) return dispatch.Error;

        if (dispatch.Value is { } existing && (existing.QuestId != row.Quest.Id || existing.IsContinuation != continuation
            || existing.LocalChildId != binding.Value.ApprenticeId || existing.Evidence != GuildDispatchEvidence.Prepared
            || existing.AccountingAssociationId != row.Quest.RunId || existing.InvocationDigest != invocationDigest)) return parked;

        if (operation.LeaseOwner is not null && operation.LeaseExpiresAt > now)
            return parked with { RequiresAttention = false };

        return new GuildLocalPendingWork(row, operation, new(mutation, row.Quest.Id, operation.Id,
            row.Quest.RunId, dispatchId, operation.Revision, invocationDigest), false);
    }
}
