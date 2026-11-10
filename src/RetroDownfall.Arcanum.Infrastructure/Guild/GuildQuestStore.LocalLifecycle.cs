using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildLocalAcceptedContinuation(GuildContinuationStored Continuation, string? Body,
    GuildMutationIdentity Mutation);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<bool>> RefreshLocalExecutionAsync(GuildLocalInferenceExecution execution,
        DateTimeOffset now, bool renew, CancellationToken ct)
    {
        GuildQuestDriver? refreshed = null;

        bool stop = false;

        // Publish the new exact driver only after commit, while the same root ordering gate is
        // still held. A physical admission cannot observe a renewed lease with its old driver.
        return authority.AdmitQuestEffectAsync<bool>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                GuildQuestDriver driver = execution.Driver;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                LongRunningOperation operation = admitted.Value.Operation;

                if (operation.LeaseOwner != driver.LeaseOwner || operation.LeaseExpiresAt is null || operation.LeaseExpiresAt <= now
                    || operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.Cancelling))
                    return RevisionConflict;

                stop = policy is GuildLocalHostPolicy { IsExecutionEnabled: false }
                    || await HasClosedAncestorAsync(connection, transaction, execution.Driver.Execution.QuestId, token).ConfigureAwait(false);

                long revision = operation.Revision;

                if (renew)
                {
                    if (revision == long.MaxValue) return ExhaustedReceipt;

                    await using SqliteCommand update = Command(connection, transaction, """
                        UPDATE LongRunningOperations SET HeartbeatAt=$now,LeaseExpiresAt=$lease,Revision=Revision+1
                        WHERE Id=$id AND Revision=$revision AND LeaseOwner=$owner
                        """);

                    Add(update, "$now", UtcInstantText.Format(now));

                    Add(update, "$lease", UtcInstantText.Format(now.AddMinutes(2)));

                    Add(update, "$id", operation.Id.ToString("N"));

                    Add(update, "$revision", revision);

                    Add(update, "$owner", driver.LeaseOwner);

                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                    revision++;
                }

                refreshed = revision == driver.OperationRevision ? driver : driver with { OperationRevision = revision };

                return true;
            }, _ => Task.FromResult(execution.TryRefreshDriver(refreshed!)
                ? Result<bool>.Success(stop) : Result<bool>.Failure(RevisionConflict)), ct);
    }

    internal Task<Result<GuildLocalAcceptedContinuation?>> ReadLocalContinuationAsync(GuildQuestDriver driver,
        Guid continuationId, CancellationToken ct, bool materialize = true) =>
        authority.InQuestTransactionAsync<GuildLocalAcceptedContinuation?>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (materialize && driver.Caller?.Kind == GuildCallerKind.HostReceipt) return ClosedDispatch;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                GuildMutationIdentity mutation;

                await using (SqliteCommand find = Command(connection, transaction, """
                    SELECT MutationId,Digest FROM guild_continuations
                    WHERE QuestId=$quest AND ContinuationId=$continuation
                        AND AcceptedRevision=(SELECT max(AcceptedRevision) FROM guild_continuations WHERE QuestId=$quest)
                        AND typeof(MutationId)='text' AND length(CAST(MutationId AS BLOB))=36
                        AND typeof(Digest)='blob' AND length(Digest)=32
                    """))
                {
                    Add(find, "$quest", Id(driver.Execution.QuestId));

                    Add(find, "$continuation", Id(continuationId));

                    await using SqliteDataReader reader = await find.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Result<GuildLocalAcceptedContinuation?>.Success(null);

                    if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid key) || key == Guid.Empty) return Unavailable;

                    mutation = new(key, Convert.ToHexString((byte[])reader.GetValue(1)));
                }

                var continuation = await ReadContinuationWithinAsync(connection, transaction, admitted.Value.Row, continuationId, token).ConfigureAwait(false);

                if (continuation.IsFailure) return continuation.Error;

                if (!materialize) return Result<GuildLocalAcceptedContinuation?>.Success(new(continuation.Value, null, mutation));

                var body = await ReadProviderContentWithinAsync(connection, transaction, continuation.Value.ContentRef, token).ConfigureAwait(false);

                if (body.IsFailure) return body.Error;

                return Result<GuildLocalAcceptedContinuation?>.Success(new(continuation.Value, body.Value, mutation));
            }, ct);

    internal Task<Result<GuildQuestStored>> ParkLocalExecutionAsync(GuildQuestDriver driver, CancellationToken ct, string? reasonCode = null) =>
        authority.InQuestTransactionAsync<GuildQuestStored>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var row = admitted.Value.Row;

                var operation = admitted.Value.Operation;

                if (row.Quest.ResultReceiptId is not null || operation.State == LongRunningOperationState.ReconciliationRequired) return row;

                if (operation.LeaseOwner != driver.LeaseOwner || operation.LeaseExpiresAt <= time.GetUtcNow()
                    || row.Quest.Revision == long.MaxValue) return RevisionConflict;

                string reason = reasonCode == ErrorCodes.Guild.LegacyToolName ? ErrorCodes.Guild.LegacyToolName
                    : row.Quest.State == QuestState.InputRequired ? ErrorCodes.Guild.AwaitingInput : ErrorCodes.Guild.LocalExecutionInterrupted;

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                    operation.Revision, driver.LeaseOwner, LongRunningOperationState.ReconciliationRequired, time.GetUtcNow(), reason, token).ConfigureAwait(false))
                    return RevisionConflict;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$id AND Revision=$revision");

                Add(update, "$id", Id(row.Quest.Id));

                Add(update, "$revision", row.Quest.Revision);

                Add(update, "$now", UtcInstantText.Format(time.GetUtcNow()));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, row.Quest.RootQuestId,
                    row.Quest.Id, GuildEventKind.QuestChanged, row.Quest.Id, row.Quest.Revision + 1, null, time.GetUtcNow(), token).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : await ReadWithinAsync(connection, transaction, row.Quest.Id, token).ConfigureAwait(false);
            }, ct);
}
