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
}
