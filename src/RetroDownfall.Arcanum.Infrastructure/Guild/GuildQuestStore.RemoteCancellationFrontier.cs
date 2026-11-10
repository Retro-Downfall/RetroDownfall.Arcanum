using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<Guid>> PrepareRemoteCancellationAsync(GuildQuestDriver driver, CancellationToken ct) =>
        authority.InQuestTransactionAsync<Guid>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, false, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (!admitted.Value.Row.Quest.CancellationRequested) return Conflict;

                Guid dispatchId = driver.Execution.QuestId;

                bool knownTask = false;

                await using (SqliteCommand latest = Command(connection, transaction, """
                    SELECT d.DispatchId FROM guild_dispatches d
                    LEFT JOIN guild_continuations c ON c.QuestId=d.QuestId AND c.ContinuationId=d.DispatchId
                    WHERE d.QuestId=$quest AND d.EvidenceCode IN(2,3) AND d.RemoteTaskId IS NOT NULL AND d.LocalChildId IS NULL
                    ORDER BY COALESCE(c.AcceptedRevision,0) DESC,d.DispatchId LIMIT 1
                    """))
                {
                    Add(latest, "$quest", Id(driver.Execution.QuestId));

                    if (await latest.ExecuteScalarAsync(token).ConfigureAwait(false) is string bound)
                    {
                        dispatchId = Guid.Parse(bound);

                        knownTask = true;
                    }
                }

                if (knownTask)
                {
                    var identity = await ReadRemoteTaskIdentityWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                    if (identity.IsFailure) return identity.Error;
                }

                var operation = admitted.Value.Operation;

                if (operation.State != LongRunningOperationState.ReconciliationRequired) return dispatchId;

                // Only observation/cancellation of an already bound task is readmitted. An
                // unknown initial acknowledgement remains uncertain and can never be resent.
                if (!knownTask || operation.LeaseOwner is not null && operation.LeaseExpiresAt > time.GetUtcNow()) return UncertainDispatch;

                if (admitted.Value.Row.Quest.Revision == long.MaxValue || operation.Revision == long.MaxValue) return ExhaustedReceipt;

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                    operation.Revision, null, LongRunningOperationState.Cancelling, time.GetUtcNow(), null, token).ConfigureAwait(false))
                    return RevisionConflict;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision");

                Add(update, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(update, "$quest", Id(driver.Execution.QuestId));
                Add(update, "$revision", admitted.Value.Row.Quest.Revision);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, driver.Execution.RootQuestId,
                    driver.Execution.QuestId, GuildEventKind.QuestChanged, driver.Execution.QuestId,
                    admitted.Value.Row.Quest.Revision + 1, null, time.GetUtcNow(), token).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : dispatchId;
            }, ct);
}
