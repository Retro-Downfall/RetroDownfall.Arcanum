using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildLocalCancellationFrontier(GuildQuestStored Quest, bool AncestorCanceled, bool ChildrenStopped);

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildLocalCancellationFrontier>> ReadLocalCancellationFrontierAsync(
        GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        if (caller is null || questId == Guid.Empty || caller.Kind is not (GuildCallerKind.Operator or GuildCallerKind.Run)
            || caller.Kind == GuildCallerKind.Run && caller.Execution?.QuestId != questId) return InvalidReceipt;

        var visible = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        return await authority.InQuestTransactionAsync<GuildLocalCancellationFrontier>(visible.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var current = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                Result allowed = await RequireVisibleAsync(connection, transaction, caller, current.Value, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                if (caller.Kind == GuildCallerKind.Run)
                {
                    GuildExecutionHandle execution = caller.Execution!;

                    if (execution.RootQuestId != current.Value.Quest.RootQuestId || execution.SignetId != current.Value.Quest.SignetId
                        || execution.RunId != current.Value.Quest.RunId) return OwnerUnavailable;

                    var live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;
                }

                await using SqliteCommand frontier = Command(connection, transaction, $"""
                    WITH RECURSIVE ancestors(Id,ParentQuestId,CancellationRequested) AS (
                        SELECT Id,ParentQuestId,CancellationRequested FROM guild_quests WHERE Id=$quest
                        UNION SELECT p.Id,p.ParentQuestId,p.CancellationRequested
                            FROM guild_quests p JOIN ancestors c ON p.Id=c.ParentQuestId),
                    children(Id,OperationId) AS (
                        SELECT Id,OperationId FROM guild_quests WHERE Id=$quest
                        UNION SELECT c.Id,c.OperationId FROM guild_quests c JOIN children p ON c.ParentQuestId=p.Id)
                    SELECT EXISTS(SELECT 1 FROM ancestors WHERE Id<>$quest AND CancellationRequested=1),
                        NOT EXISTS(SELECT 1 FROM children t JOIN guild_quests c ON c.Id=t.Id
                            WHERE c.Id<>$quest AND {GuildQuestRetentionPins.StoppedExecutionUnresolved})
                        AND NOT EXISTS(SELECT 1 FROM children t JOIN guild_quests c ON c.Id=t.Id
                            WHERE {GuildQuestRetentionPins.ExternalWorkUnresolved})
                    """);

                Add(frontier, "$quest", Id(questId));

                await using SqliteDataReader reader = await frontier.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable;

                return new GuildLocalCancellationFrontier(current.Value, reader.GetInt64(0) == 1, reader.GetInt64(1) == 1);
            }, ct).ConfigureAwait(false);
    }
}
