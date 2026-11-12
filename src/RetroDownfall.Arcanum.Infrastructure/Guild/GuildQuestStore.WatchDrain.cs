using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<bool>> IsWatchTreeDrainedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestDto owner, CancellationToken ct)
    {
        if (owner.State is not (QuestState.Completed or QuestState.Failed or QuestState.Canceled or QuestState.Rejected)
            || owner.ReconciliationRequired || owner.CancellationRequested && !owner.CancellationConverged)
            return false;

        Result<GuildQuestTreePage> page = await ReadTreePageWithinAsync(connection, transaction, owner, null, 1, null, ct).ConfigureAwait(false);

        if (page.IsFailure) return page.Error;

        if (page.Value.Summary.ActiveExecutionCount != 0 || page.Value.Summary.ReconciliationRequiredCount != 0) return false;

        await using SqliteCommand command = Command(connection, transaction, $"""
            WITH RECURSIVE members(Id) AS (
                SELECT Id FROM guild_quests WHERE Id=$subtree AND RootQuestId=$root
                UNION SELECT q.Id FROM guild_quests q INDEXED BY idx_guild_quests_parent JOIN members m ON q.ParentQuestId=m.Id
                    WHERE q.RootQuestId=$root)
            SELECT EXISTS(SELECT 1 FROM guild_result_outbox o JOIN members m ON m.Id=o.QuestId
                WHERE typeof(o.DeliveryStateCode)<>'integer' OR o.DeliveryStateCode NOT IN(2,3))
                OR EXISTS(SELECT 1 FROM guild_portal_outbox o JOIN members m ON m.Id=o.QuestId
                WHERE typeof(o.DeliveryStateCode)<>'integer' OR o.DeliveryStateCode NOT IN(2,3))
                OR EXISTS(SELECT 1 FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
                    WHERE i.RecipientQuestId IS NOT NULL
                    AND (i.RecipientQuestId IN(SELECT Id FROM members) OR e.QuestId IN(SELECT Id FROM members))
                    AND {GuildQuestRetentionPins.UnresolvedPortalInbox})
            """);

        Add(command, "$subtree", Id(owner.Id));

        Add(command, "$root", Id(owner.RootQuestId));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is long pending
            ? pending == 0 : Unavailable;
    }
}
