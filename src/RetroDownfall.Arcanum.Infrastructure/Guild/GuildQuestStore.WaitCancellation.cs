using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<bool>> PrepareStoppedLocalWaitCancellationAsync(GuildQuestDriver driver, bool requireLease, CancellationToken ct) =>
        authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, requireLease, true, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                GuildQuestStored row = admitted.Value.Row;

                LongRunningOperation operation = admitted.Value.Operation;

                if (!row.Quest.CancellationRequested || row.Quest.ResultReceiptId is not null) return false;

                bool parked = operation.State == LongRunningOperationState.ReconciliationRequired
                    && operation.TerminalErrorCode == ErrorCodes.Guild.AwaitingInput;

                if (operation.State != LongRunningOperationState.Cancelling && !parked) return false;

                if (!requireLease && operation.LeaseOwner is not null && operation.LeaseExpiresAt > time.GetUtcNow()) return false;

                if ((await RequireWaitFrontierWithinAsync(connection, transaction, row.Quest.Id, token).ConfigureAwait(false)).IsFailure)
                    return false;

                await using SqliteCommand proof = Command(connection, transaction, $"""
                    SELECT EXISTS(SELECT 1 FROM guild_quests c JOIN guild_local_wait_checkpoints w
                        ON w.QuestId=c.Id AND w.QuestionId=c.QuestionId AND w.ContinuationId=c.ContinuationId
                        JOIN guild_local_executions b ON b.QuestId=c.Id
                        JOIN guild_dispatches dispatch ON dispatch.DispatchId=w.DispatchId AND dispatch.QuestId=c.Id
                        WHERE c.Id=$quest AND c.LastNonterminalStateCode=2 AND c.WaitReasonCode=0
                            AND c.QuestionId<>'00000000-0000-0000-0000-000000000000'
                            AND c.ContinuationId<>'00000000-0000-0000-0000-000000000000'
                            AND dispatch.EvidenceCode IN(3,4) AND dispatch.LocalChildId=b.ApprenticeId
                            AND {WaitCheckpointProof} AND {GuildLocalExecutionStore.BindingProof}
                            AND NOT {GuildQuestRetentionPins.ExternalWorkUnresolved}
                            AND NOT EXISTS(SELECT 1 FROM guild_continuations answer
                                WHERE answer.QuestId=c.Id AND answer.ContinuationId=c.ContinuationId))
                    """);

                Add(proof, "$quest", Id(row.Quest.Id));

                if (!Equals(1L, await proof.ExecuteScalarAsync(token).ConfigureAwait(false))) return false;

                if (!parked) return true;

                // A proven persisted question can stop without re-entering its engine. A generic
                // interrupted execution never acquires this readmission, even beside an old checkpoint.
                if (row.Quest.Revision == long.MaxValue || operation.Revision == long.MaxValue) return ExhaustedReceipt;

                DateTimeOffset now = time.GetUtcNow();

                if (!await LongRunningOperationStore.TryTransitionWithinAsync(connection, transaction, operation.Id,
                    operation.Revision, operation.LeaseOwner, LongRunningOperationState.Cancelling, now, null, token).ConfigureAwait(false))
                    return RevisionConflict;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision");

                Add(update, "$quest", Id(row.Quest.Id));

                Add(update, "$revision", row.Quest.Revision);

                Add(update, "$now", UtcInstantText.Format(now));

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, row.Quest.RootQuestId,
                    row.Quest.Id, GuildEventKind.QuestChanged, row.Quest.Id, row.Quest.Revision + 1, null, now, token).ConfigureAwait(false);

                return projected.IsFailure ? Result<bool>.Failure(projected.Error) : Result<bool>.Success(true);
            }, ct);
}
