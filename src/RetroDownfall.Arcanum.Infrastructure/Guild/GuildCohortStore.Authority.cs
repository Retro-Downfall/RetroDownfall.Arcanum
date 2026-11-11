using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal static async Task<Result<GuildGrant?>> ReadAuthorityConstraintWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildSignetRecord signet, CancellationToken ct)
    {
        Guid cohortId;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT m.CohortId,m.AgentId,m.PrimaryVotingQuestId,m.StateCode,a.Active,a.AssociationKindCode
            FROM guild_cohort_member_quests a JOIN guild_cohort_members m ON m.Id=a.MemberId
            WHERE a.QuestId=$quest
            """))
        {
            Add(command, "$quest", Id(signet.Binding.QuestId));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildGrant?>.Success(null);

            // Retain withdrawn associations as constraints. Removing membership must never
            // turn the same Signet, or one of its descendants, into unconstrained authority.
            if (!Guid.TryParse(reader.GetString(1), out Guid agentId) || agentId != signet.Binding.AgentId
                || reader.GetInt32(3) != 1 || reader.GetInt32(4) != 1 || reader.GetInt32(5) is not (1 or 2)
                || reader.GetInt32(5) == 1 && (!Guid.TryParse(reader.GetString(2), out Guid primaryId) || primaryId != signet.Binding.QuestId))
                return new Error(ErrorCodes.Guild.AuthorityRevoked, "The Cohort membership no longer authorizes effects.");

            if (!Guid.TryParse(reader.GetString(0), out cohortId) || await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;
        }

        Result<GuildCohortStored> cohort = await ReadWithinAsync(connection, transaction, cohortId, ct).ConfigureAwait(false);

        if (cohort.IsFailure) return cohort.Error;

        if (cohort.Value.Cohort.RootQuestId != signet.Binding.RootQuestId) return Unavailable;

        if (cohort.Value.Cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed))
            return new Error(ErrorCodes.Guild.AuthorityRevoked, "The Cohort decision has sealed member effects.");

        return Result<GuildGrant?>.Success(cohort.Value.Cohort.CohortGrant);
    }

    internal static async Task<Result<GuildSignetRecord?>> ReadAwardPrimaryWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildSignetRecord signet, CancellationToken ct)
    {
        Guid primary;
        Guid boundSignet;
        long generation;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT m.PrimaryVotingQuestId,w.PrimaryQuestId,w.PrimarySignetId,w.PrimaryRevocationGeneration,w.MemberId,a.MemberId,
                EXISTS(SELECT 1 FROM guild_work_offers o JOIN guild_work_bids b ON b.Id=w.BidId
                    JOIN guild_quests q ON q.Id=w.QuestId
                    WHERE o.Id=w.OfferId AND o.CohortId=m.CohortId AND o.StateCode=2 AND o.AwardId=w.Id AND o.QuestId=q.Id
                        AND q.ParentQuestId=o.IssuerQuestId AND q.AgentId=m.AgentId AND q.SignetId=$signet
                        AND b.OfferId=o.Id AND b.MemberId=m.Id AND b.AgentId=m.AgentId AND b.StateCode=1)
            FROM guild_cohort_member_quests a JOIN guild_cohort_members m ON m.Id=a.MemberId
            LEFT JOIN guild_work_awards w ON w.QuestId=a.QuestId
            WHERE a.QuestId=$quest AND a.AssociationKindCode=2
            """))
        {
            Add(command, "$quest", Id(signet.Binding.QuestId));
            Add(command, "$signet", Id(signet.Binding.SignetId));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildSignetRecord?>.Success(null);

            if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)
                || !Guid.TryParse(reader.GetString(0), out primary) || primary == Guid.Empty
                || !Guid.TryParse(reader.GetString(1), out Guid storedPrimary) || storedPrimary != primary
                || !Guid.TryParse(reader.GetString(2), out boundSignet) || boundSignet == Guid.Empty
                || reader.GetValue(3) is not long value || value < 0 || reader.GetString(4) != reader.GetString(5)
                || reader.GetValue(6) is not long complete || complete != 1) return Unavailable;

            generation = value;

            if (await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;
        }

        Result<GuildSignetRecord> bound = await GuildSignetStore.ReadAsync(connection, transaction, boundSignet, false, ct).ConfigureAwait(false);

        if (bound.IsFailure) return bound.Error;

        if (bound.Value.Binding.SignetId != boundSignet || bound.Value.Binding.QuestId != primary) return Unavailable;

        if (bound.Value.RevocationGeneration != generation)
            return new Error(ErrorCodes.Guild.AuthorityRevoked, "The winning primary no longer authorizes award work.");

        return Result<GuildSignetRecord?>.Success(bound.Value);
    }
}
