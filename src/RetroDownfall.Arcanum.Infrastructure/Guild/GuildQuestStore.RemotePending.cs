using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private static async Task<Result<GuildLocalPendingWork>> ReadRemotePendingWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored row, LongRunningOperation operation, DateTimeOffset now, CancellationToken ct)
    {
        GuildLocalPendingWork parked = new(row, operation, null, true, true);

        if (row.Quest.ResultReceiptId is not null) return parked with { RequiresAttention = false };

        if (row.Retired || row.Quest.CancellationRequested || operation.State is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed or LongRunningOperationState.Abandoned or LongRunningOperationState.Cancelling) return parked;

        if (row.Quest.GoalRef.ContentErased || await HasClosedAncestorAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false)) return parked;

        var input = await GuildRemoteExecutionStore.ReadInputWithinAsync(connection, transaction, row.Quest.Id, ct).ConfigureAwait(false);

        if (input.IsFailure) return input.Error;

        Guid dispatchId = row.Quest.Id;

        GuildMutationIdentity mutation = new(row.MutationId, row.IntentDigest);

        string invocationDigest = row.Quest.GoalRef.Digest;

        await using (SqliteCommand latest = Command(connection, transaction,
            "SELECT ContinuationId,MutationId,Digest FROM guild_continuations WHERE QuestId=$quest ORDER BY AcceptedRevision DESC LIMIT 1"))
        {
            Add(latest, "$quest", Id(row.Quest.Id));

            await using SqliteDataReader reader = await latest.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                dispatchId = Guid.Parse(reader.GetString(0));

                mutation = new(Guid.Parse(reader.GetString(1)), Convert.ToHexString((byte[])reader.GetValue(2)));
            }
        }

        if (dispatchId != row.Quest.Id)
        {
            var continuation = await ReadContinuationWithinAsync(connection, transaction, row, dispatchId, ct).ConfigureAwait(false);

            if (continuation.IsFailure || continuation.Value.ContentRef.ContentErased) return parked;

            invocationDigest = continuation.Value.ContentRef.Digest;
        }

        var dispatch = await ReadDispatchWithinAsync(connection, transaction, dispatchId, ct).ConfigureAwait(false);

        if (dispatch.IsFailure) return dispatch.Error;

        if (dispatch.Value is { } current)
        {
            if (current.QuestId != row.Quest.Id || current.InvocationDigest != invocationDigest || current.LocalChildId is not null
                || current.AccountingAssociationId != row.Quest.RunId || current.Evidence == GuildDispatchEvidence.ResolvedNoDispatch) return parked;

            // Unknown acceptance cannot authorize another message. Known tasks remain queryable, including input waits.
            if (current.Evidence != GuildDispatchEvidence.Prepared && current.RemoteTaskId is null) return parked;
        }
        else if (row.Quest.State == QuestState.InputRequired) return parked;

        if (operation.LeaseOwner is not null && operation.LeaseExpiresAt > now)
            return parked with { RequiresAttention = false };

        return new GuildLocalPendingWork(row, operation, new(mutation, row.Quest.Id, operation.Id, row.Quest.RunId,
            dispatchId, operation.Revision, invocationDigest), false, true);
    }

    internal Task<Result<bool>> IsRemoteExecutionAsync(GuildQuestDriver driver, CancellationToken ct) =>
        authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

            return admitted.IsFailure ? admitted.Error
                : await IsRemoteWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);
        }, ct);
}
