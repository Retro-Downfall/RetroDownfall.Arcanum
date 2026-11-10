using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private static async Task<Result<string?>> ReadRemoteStopReasonWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using SqliteCommand read = Command(connection, transaction,
            "SELECT StopReasonCode FROM guild_remote_effects WHERE EffectId=$quest AND QuestId=$quest");

        Add(read, "$quest", Id(questId));

        object? reason = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return reason is null or DBNull ? Result<string?>.Success(null)
            : reason is string code && code == ErrorCodes.Sending.TaskRejected ? Result<string?>.Success(code) : Unavailable;
    }

    internal async Task<Result> RecordRemoteProtocolStopAsync(GuildQuestDriver driver, Guid dispatchId,
        A2AGuildTaskObservation observation, CancellationToken ct)
    {
        if (GuildRemoteStateProjection.Observe(observation.State).State != QuestState.InputRequired) return InvalidReceipt;

        var recorded = await authority.InQuestTransactionAsync<bool>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var input = await ReadRemoteInvocationWithinAsync(connection, transaction, driver.Execution.QuestId, dispatchId, token).ConfigureAwait(false);

                if (input.IsFailure) return input.Error;

                if (input.Value.Mode == A2ADispatchMode.Continuable) return Conflict;

                Result physical = await ApplyRemoteEvidenceWithinAsync(connection, transaction, admitted.Value.Row,
                    new(dispatchId, observation), false, token).ConfigureAwait(false);

                if (physical.IsFailure) return physical.Error;

                await using SqliteCommand stop = Command(connection, transaction,
                    "UPDATE guild_remote_effects SET StopReasonCode=$reason WHERE EffectId=$quest AND QuestId=$quest AND StopReasonCode IS NULL");

                Add(stop, "$reason", ErrorCodes.Sending.TaskRejected);
                Add(stop, "$quest", Id(driver.Execution.QuestId));

                if (await stop.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 0)
                {
                    var prior = await ReadRemoteStopReasonWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                    return prior.IsFailure ? prior.Error : prior.Value == ErrorCodes.Sending.TaskRejected ? true : Conflict;
                }

                if (admitted.Value.Row.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

                await using SqliteCommand update = Command(connection, transaction,
                    "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision");

                Add(update, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(update, "$quest", Id(driver.Execution.QuestId));
                Add(update, "$revision", admitted.Value.Row.Quest.Revision);

                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return RevisionConflict;

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, driver.Execution.RootQuestId,
                    driver.Execution.QuestId, GuildEventKind.QuestChanged, driver.Execution.QuestId,
                    admitted.Value.Row.Quest.Revision + 1, null, time.GetUtcNow(), token).ConfigureAwait(false);

                return projected.IsFailure ? projected.Error : true;
            }, ct).ConfigureAwait(false);

        return recorded.IsFailure ? recorded.Error : Result.Success();
    }
}
