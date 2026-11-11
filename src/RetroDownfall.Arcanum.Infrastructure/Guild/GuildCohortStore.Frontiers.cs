using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    /// <summary>Processes one association and at most 99 pending audience dispositions per transaction.</summary>
    private async Task<Result<bool>> AdvanceStopFrontierWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohortId, DateTimeOffset now, CancellationToken ct)
    {
        Guid frontierId;
        Guid? onlyMember;
        long afterMember;
        long afterAssociation;

        await using (SqliteCommand read = Command(connection, transaction, """
            SELECT Id,MemberId,LastMemberSequence,LastAssociationSequence FROM guild_cohort_stop_frontiers
            WHERE Completed=0 AND CohortId=$cohort ORDER BY Id LIMIT 1
            """))
        {
            Add(read, "$cohort", Id(cohortId));

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return false;

            frontierId = Guid.Parse(reader.GetString(0));
            onlyMember = reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1));
            afterMember = reader.GetInt64(2);
            afterAssociation = reader.GetInt64(3);
        }

        Guid member;
        Guid quest;
        long memberSequence;
        long associationSequence;

        await using (SqliteCommand next = Command(connection, transaction, """
            SELECT m.Id,a.QuestId,m.AdmissionSequence,a.AssociationSequence
            FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id
            WHERE m.CohortId=$cohort AND ($member IS NULL OR m.Id=$member)
              AND (m.AdmissionSequence>$afterMember OR (m.AdmissionSequence=$afterMember AND a.AssociationSequence>$afterAssociation))
            ORDER BY m.AdmissionSequence,a.AssociationSequence LIMIT 1
            """))
        {
            Add(next, "$cohort", Id(cohortId));
            Add(next, "$member", onlyMember is { } selected ? Id(selected) : null);
            Add(next, "$afterMember", afterMember);
            Add(next, "$afterAssociation", afterAssociation);

            await using SqliteDataReader reader = await next.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);

                await using SqliteCommand completed = Command(connection, transaction,
                    "UPDATE guild_cohort_stop_frontiers SET Completed=1 WHERE Id=$id AND Completed=0");

                Add(completed, "$id", Id(frontierId));

                return await completed.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? true : Unavailable;
            }

            member = Guid.Parse(reader.GetString(0));
            quest = Guid.Parse(reader.GetString(1));
            memberSequence = reader.GetInt64(2);
            associationSequence = reader.GetInt64(3);
        }

        Result stop = await quests.RequestCohortCancellationWithinAsync(connection, transaction, quest, ct).ConfigureAwait(false);

        if (stop.IsFailure) return stop.Error;

        string recipient = "quest:" + Id(quest);

        const string audience = "i.RecipientKey=$recipientKey AND EXISTS(SELECT 1 FROM guild_portal_cohort_audience a "
            + "WHERE a.MemberId=$identity AND a.QuestId=i.RecipientQuestId AND a.EnvelopeId=i.EnvelopeId)";

        Result withheld = await GuildPortalWithholding.WithholdWithinAsync(connection, transaction, string.Empty,
            audience, member, ErrorCodes.Guild.AuthorityRevoked, now, ct, maximumBatches: 1, batchSize: 99,
            recipientKey: recipient, updateResultOutbox: false).ConfigureAwait(false);

        if (withheld.IsFailure) return withheld.Error;

        await using (SqliteCommand remains = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_portal_cohort_audience a
              JOIN guild_inbox i ON i.EnvelopeId=a.EnvelopeId AND i.RecipientKey=$recipient
              WHERE a.MemberId=$member AND a.QuestId=$quest AND i.DeliveryStateCode=1)
            """))
        {
            Add(remains, "$member", Id(member));
            Add(remains, "$quest", Id(quest));
            Add(remains, "$recipient", recipient);

            if (Equals(1L, await remains.ExecuteScalarAsync(ct).ConfigureAwait(false))) return true;
        }

        await using SqliteCommand advance = Command(connection, transaction, """
            UPDATE guild_cohort_stop_frontiers SET LastMemberSequence=$member,LastAssociationSequence=$association
            WHERE Id=$id AND Completed=0 AND LastMemberSequence=$oldMember AND LastAssociationSequence=$oldAssociation
            """);

        Add(advance, "$member", memberSequence);
        Add(advance, "$association", associationSequence);
        Add(advance, "$id", Id(frontierId));
        Add(advance, "$oldMember", afterMember);
        Add(advance, "$oldAssociation", afterAssociation);

        return await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? true : Unavailable;
    }
}
