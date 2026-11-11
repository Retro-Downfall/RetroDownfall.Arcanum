using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCohortStored(CohortDto Cohort);

internal sealed partial class GuildCohortStore(GuildStore guild, SignetAuthority authority, GuildQuestStore quests,
    GuildBudgetLedger budgets, IGuildQuestHostPolicy policy, TimeProvider time)
{
    private static readonly Error Unavailable = new(ErrorCodes.Guild.AuthorityUnavailable, "Cohort evidence is unavailable or inconsistent.");

    private static readonly Error Conflict = new(ErrorCodes.Guild.RevisionConflict, "The Cohort revision or membership changed.");

    internal static Error RevisionConflict(CohortDto cohort) => new(ErrorCodes.Guild.RevisionConflict,
        $"The Cohort is {cohort.State} at revision {cohort.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");

    private static readonly Error Forbidden = new(ErrorCodes.Guild.Forbidden, "The Cohort admission is not authorized.");

    internal static async Task<Result<GuildCohortStored>> ReadWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohortId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT Id,Name,RootQuestId,IssuerQuestId,CampaignId,PolicyCode,Quorum,MimeType,StateCode,Revision,
                MembershipVersion,ClosedMembershipVersion,
                CASE WHEN typeof(GrantJson)='text' AND length(CAST(GrantJson AS BLOB))<=65536 THEN GrantJson END,
                AllocationAccountId,EventWatermark,Retired
            FROM guild_cohorts WHERE Id=$id
            """);

        Add(command, "$id", Id(cohortId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(12)) return Unavailable;

        if (reader.GetValue(15) is not long retired || retired is not (0 or 1)) return Unavailable;

        if (retired == 1) return new Error(ErrorCodes.Guild.OwnerUnavailable, "The Cohort owner is unavailable.");

        try
        {
            string serialized = reader.GetString(12);

            GuildGrant? grant = JsonSerializer.Deserialize(serialized, GuildJsonContext.Default.GuildGrant);

            if (grant is null || GuildGrantPolicy.Validate(grant).IsFailure) return Unavailable;

            Result<string> canonical = GuildSignetGrantJson.Encode(grant);

            if (canonical.IsFailure || canonical.Value != serialized) return Unavailable;

            GuildConvergenceKind kind = (GuildConvergenceKind)reader.GetInt32(5);

            GuildCohortState state = (GuildCohortState)reader.GetInt32(8);

            if (!Enum.IsDefined(kind) || !Enum.IsDefined(state)) return Unavailable;

            CohortDto dto = new(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)),
                new(kind, reader.IsDBNull(6) ? null : reader.GetInt32(6)), new(reader.GetString(7)), state,
                reader.GetInt64(9), reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetInt64(11), grant,
                Guid.Parse(reader.GetString(13)), reader.GetInt64(14));

            return new GuildCohortStored(dto);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidCastException or OverflowException)
        {
            return Unavailable;
        }
    }

    internal static async Task<Result> ValidateAdmissionWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCohortStored cohort, Guid issuerQuestId, long expectedRevision, Guid agentId, CancellationToken ct)
    {
        if (cohort.Cohort.IssuerQuestId != issuerQuestId || agentId == Guid.Empty) return Forbidden;

        if (cohort.Cohort.State != GuildCohortState.Open || cohort.Cohort.Revision != expectedRevision) return RevisionConflict(cohort.Cohort);

        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM guild_cohort_members WHERE CohortId=$cohort AND AgentId=$agent)");

        Add(command, "$cohort", Id(cohort.Cohort.Id));

        Add(command, "$agent", Id(agentId));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Forbidden : Result.Success();
    }

    internal static async Task<Result<CohortMemberDto>> AddPrimaryWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCohortStored cohort, Guid questId, Guid agentId, DateTimeOffset now, CancellationToken ct)
    {
        if (cohort.Cohort.Revision == long.MaxValue || cohort.Cohort.MembershipVersion == long.MaxValue) return Unavailable;

        long sequence = cohort.Cohort.MembershipVersion + 1;

        Guid member = Guid.NewGuid();

        Result<long> eventSequence = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
            cohort.Cohort.RootQuestId, cohort.Cohort.IssuerQuestId, GuildEventKind.MembershipChanged,
            cohort.Cohort.Id, cohort.Cohort.Revision + 1, null, now, ct).ConfigureAwait(false);

        if (eventSequence.IsFailure) return eventSequence.Error;

        await using (SqliteCommand update = Command(connection, transaction, """
            UPDATE guild_cohorts SET MembershipVersion=$sequence,ActiveMemberCount=ActiveMemberCount+1,EventWatermark=$watermark,Revision=Revision+1
            WHERE Id=$cohort AND StateCode=1 AND Revision=$revision AND MembershipVersion=$prior AND ActiveMemberCount<9223372036854775807
            """))
        {
            Add(update, "$sequence", sequence);
            Add(update, "$watermark", eventSequence.Value);
            Add(update, "$cohort", Id(cohort.Cohort.Id));
            Add(update, "$revision", cohort.Cohort.Revision);
            Add(update, "$prior", cohort.Cohort.MembershipVersion);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;
        }

        await using (SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_cohort_members(Id,CohortId,AgentId,PrimaryVotingQuestId,AdmissionSequence,StateCode)
            SELECT $member,$cohort,$agent,q.Id,$sequence,1 FROM guild_quests q
            WHERE q.Id=$quest AND q.AgentId=$agent AND q.ParentQuestId=$issuer AND q.RootQuestId=$root
            """))
        {
            Add(insert, "$member", Id(member));
            Add(insert, "$cohort", Id(cohort.Cohort.Id));
            Add(insert, "$agent", Id(agentId));
            Add(insert, "$quest", Id(questId));
            Add(insert, "$issuer", Id(cohort.Cohort.IssuerQuestId));
            Add(insert, "$root", Id(cohort.Cohort.RootQuestId));
            Add(insert, "$sequence", sequence);

            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Forbidden;
        }

        await using (SqliteCommand association = Command(connection, transaction, """
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            VALUES($member,$quest,1,1,1)
            """))
        {
            Add(association, "$member", Id(member));
            Add(association, "$quest", Id(questId));

            _ = await association.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return new CohortMemberDto(member, cohort.Cohort.Id, questId, agentId, sequence,
            GuildCohortMemberState.Active, false, null, 1);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}
