using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private static bool IsDistributionEvent(GuildEventKind kind) => kind is GuildEventKind.OfferChanged or GuildEventKind.BidChanged
        or GuildEventKind.AwardCommitted or GuildEventKind.NoteAppended;

    // Resolve the retained object and its exact current audience before exempting an event from
    // ordinary Quest-subtree visibility. The operator has already passed root owner admission.
    private static async Task<Guid?> DistributionEventCohortWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        PortalEvent row, Guid root, Guid? actor, CancellationToken ct)
    {
        string sql = row.Kind switch
        {
            GuildEventKind.OfferChanged => """
                SELECT c.Id FROM guild_work_offers o JOIN guild_cohorts c ON c.Id=o.CohortId
                WHERE o.Id=$object AND o.IssuerQuestId=$source AND c.RootQuestId=$root
                  AND ($operator=1 OR o.IssuerQuestId=$actor OR (c.StateCode IN (1,2) AND EXISTS(
                    SELECT 1 FROM guild_work_offer_recipients r JOIN guild_cohort_members m ON m.Id=r.MemberId AND m.StateCode=1
                    JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=$actor AND a.Active=1
                    JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
                    WHERE r.OfferId=o.Id AND m.CohortId=c.Id)))
                """,
            GuildEventKind.BidChanged => """
                SELECT c.Id FROM guild_work_bids b JOIN guild_work_offers o ON o.Id=b.OfferId JOIN guild_cohorts c ON c.Id=o.CohortId
                WHERE b.Id=$object AND c.RootQuestId=$root AND EXISTS(SELECT 1 FROM guild_cohort_member_quests source
                    WHERE source.MemberId=b.MemberId AND source.QuestId=$source)
                  AND ($operator=1 OR o.IssuerQuestId=$actor OR (c.StateCode IN (1,2) AND EXISTS(
                    SELECT 1 FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=$actor AND a.Active=1
                    JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
                    WHERE m.Id=b.MemberId AND m.CohortId=c.Id AND m.StateCode=1)))
                """,
            GuildEventKind.AwardCommitted => """
                SELECT c.Id FROM guild_work_awards w JOIN guild_work_offers o ON o.Id=w.OfferId JOIN guild_cohorts c ON c.Id=o.CohortId
                WHERE w.Id=$object AND o.IssuerQuestId=$source AND c.RootQuestId=$root
                  AND ($operator=1 OR o.IssuerQuestId=$actor OR (c.StateCode IN (1,2) AND EXISTS(
                    SELECT 1 FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=$actor AND a.Active=1
                    JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
                    WHERE m.Id=w.MemberId AND m.CohortId=c.Id AND m.StateCode=1)))
                """,
            GuildEventKind.NoteAppended => """
                SELECT c.Id FROM guild_cohort_ledger n JOIN guild_cohorts c ON c.Id=n.CohortId
                WHERE n.Id=$object AND c.RootQuestId=$root AND COALESCE(n.AuthorQuestId,c.IssuerQuestId)=$source
                  AND ($operator=1 OR c.IssuerQuestId=$actor OR (c.StateCode IN (1,2) AND EXISTS(
                    SELECT 1 FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=$actor AND a.Active=1
                    JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
                    WHERE m.CohortId=c.Id AND m.StateCode=1)))
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };

        if (row.Source != GuildPortalEventSource.Quest || row.EnvelopeId is not null) return null;

        await using SqliteCommand command = Command(connection, transaction, sql);

        Add(command, "$object", Id(row.ObjectId));
        Add(command, "$source", Id(row.QuestId));
        Add(command, "$root", Id(root));
        Add(command, "$actor", Id(actor));
        Add(command, "$operator", actor is null ? 1 : 0);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string value
            && Guid.TryParseExact(value, "D", out Guid cohort) && cohort != Guid.Empty ? cohort : null;
    }
}
