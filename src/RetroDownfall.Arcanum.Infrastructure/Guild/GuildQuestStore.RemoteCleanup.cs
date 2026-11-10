using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildQuestDriver>> CreateRemoteCleanupDriverAsync(GuildCallerContext owner, Guid questId, CancellationToken ct)
    {
        var visible = await GetAsync(owner, questId, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        return await authority.InQuestTransactionAsync<GuildQuestDriver>(visible.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var row = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (row.IsFailure) return row.Error;

                if (!await IsRemoteWithinAsync(connection, transaction, questId, token).ConfigureAwait(false)
                    || policy is not GuildLocalHostPolicy host) return Unavailable;

                var operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, row.Value.Quest.OperationId, token).ConfigureAwait(false);

                if (operation is null) return Unavailable;

                return await host.CreateRemoteCleanupWithinAsync(owner, row.Value, operation, connection, transaction, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<bool>> RequiresRemoteCancellationAsync(GuildCallerContext owner, Guid questId, CancellationToken ct)
    {
        var visible = await GetAsync(owner, questId, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        return await authority.InQuestTransactionAsync<bool>(visible.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var row = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (row.IsFailure) return row.Error;

                Result allowed = await RequireVisibleAsync(connection, transaction, owner, row.Value, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                if (owner.Kind != GuildCallerKind.Operator || row.Value.Quest.ResultReceiptId is not null
                    || !await IsRemoteWithinAsync(connection, transaction, questId, token).ConfigureAwait(false)) return false;

                await using SqliteCommand query = Command(connection, transaction, """
                    WITH RECURSIVE lineage(QuestId,IssuerSignetId,CancellationRequested,Revoked) AS (
                        SELECT QuestId,IssuerSignetId,CancellationRequested,Revoked FROM guild_signets WHERE QuestId=$quest
                        UNION SELECT p.QuestId,p.IssuerSignetId,p.CancellationRequested,p.Revoked
                            FROM guild_signets p JOIN lineage c ON p.SignetId=c.IssuerSignetId)
                    SELECT EXISTS(SELECT 1 FROM lineage WHERE CancellationRequested=1 OR Revoked=1)
                        OR EXISTS(SELECT 1 FROM guild_remote_effects WHERE QuestId=$quest AND StopReasonCode IS NOT NULL)
                    """);

                Add(query, "$quest", Id(questId));

                return Equals(1L, await query.ExecuteScalarAsync(token).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);
    }
}
