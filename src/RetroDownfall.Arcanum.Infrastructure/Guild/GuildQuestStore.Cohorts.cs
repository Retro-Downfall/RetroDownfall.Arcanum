using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private Task<Result<GuildQuestStored>> InCohortIssueTransactionAsync(Guid rootQuestId, GuildChildToolAdmission? toolAdmission,
        GuildCohortIssue? cohortIssue,
        Func<SqliteConnection, SqliteTransaction, SignetAuthority.QuestTransaction, CancellationToken, Task<Result<GuildQuestStored>>> action,
        CancellationToken ct) => authority.InQuestTransactionAsync<GuildQuestStored>(rootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var result = await action(connection, transaction, signets, token).ConfigureAwait(false);

                if (result.IsFailure || toolAdmission is null || cohortIssue is null) return result;

                if (cohortIssue.Member is null) return Unavailable;

                Result projection = toolAdmission.ValidateProjectionAllocation(JsonSerializer.SerializeToElement(
                    cohortIssue.Member, GuildJsonContext.Default.CohortMemberDto));

                return projection.IsFailure ? projection.Error : result;
            }, ct);

    internal async Task<Result<GuildQuestStored>> IssueCohortMemberAsync(GuildCallerContext caller,
        GuildIssuanceIdentity issuance, Guid cohortId, long expectedCohortRevision,
        GuildIssueChildRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller?.Execution is not { } execution || caller.Kind != GuildCallerKind.Run || issuance is null
            || request is null || cohortId == Guid.Empty || expectedCohortRevision < 1 || expectedCohortRevision == long.MaxValue)
            return InvalidReceipt;

        if (toolAdmission is not null)
        {
            if (!toolAdmission.Registry.Owns(toolAdmission) || !ReferenceEquals(toolAdmission.Registry.Parent.Store, this)
                || !ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                || issuance.IssuingStepId != toolAdmission.IssuingStepId || issuance.LogicalCallId != toolAdmission.LogicalCallId
                || !GuildCohortToolArguments.MatchesAdmission(toolAdmission, cohortId, expectedCohortRevision, request))
                return InvalidReceipt;

            var intent = ToolIssueIntent(execution.QuestId, request);

            if (intent.IsFailure) return intent.Error;

            if (!toolAdmission.BindIntent(CohortIssueIntent(cohortId, expectedCohortRevision, intent.Value))) return Conflict;
        }

        GuildCohortIssue context = new(cohortId, expectedCohortRevision);

        var accepted = await IssueAsync(caller, issuance, null, execution.QuestId, request, ct,
            toolAdmission, cohortIssue: context).ConfigureAwait(false);

        if (accepted.IsSuccess && toolAdmission is not null)
        {
            if (context.Member is null) return Unavailable;

            toolAdmission.RecordCommittedProjection(accepted.Value.Quest,
                JsonSerializer.SerializeToElement(context.Member, GuildJsonContext.Default.CohortMemberDto));
        }

        return accepted;
    }

    private sealed class GuildCohortIssue(Guid id, long expectedRevision)
    {
        internal Guid Id { get; } = id;

        internal long ExpectedRevision { get; } = expectedRevision;

        internal CohortMemberDto? Member { get; set; }
    }

    private static string CohortIssueIntent(Guid cohortId, long expectedRevision, string childIntent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant(
            $"guild-cohort-issue-v1\n{cohortId:D}\n{expectedRevision}\n{childIntent}"))));

    private static GuildRequestedGrant CohortGrantRequest(GuildGrant grant) => new(new(true, grant.Workspaces),
        new(true, grant.Tools), new(true, grant.Memory), new(true, grant.Budget), new(true, grant.Delegation), new(true, grant.ExpiresAtUtc));

    private static async Task<Result<CohortMemberDto>> ReadCohortMemberWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid cohortId, GuildQuestStored quest, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT m.Id,m.AgentId,m.AdmissionSequence,m.StateCode,m.RemovalSequence,
                c.ClosedMembershipVersion,(SELECT count(*) FROM guild_cohort_member_quests a WHERE a.MemberId=m.Id)
            FROM guild_cohort_members m JOIN guild_cohorts c ON c.Id=m.CohortId
            JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=m.PrimaryVotingQuestId AND a.AssociationKindCode=1
            WHERE m.CohortId=$cohort AND m.PrimaryVotingQuestId=$quest AND m.AgentId=$agent AND c.RootQuestId=$root
            """);

        Add(command, "$cohort", Id(cohortId));
        Add(command, "$quest", Id(quest.Quest.Id));
        Add(command, "$agent", Id(quest.Quest.AgentId));
        Add(command, "$root", Id(quest.Quest.RootQuestId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Conflict;

        long? removed = reader.IsDBNull(4) ? null : reader.GetInt64(4);

        long? closed = reader.IsDBNull(5) ? null : reader.GetInt64(5);

        return new CohortMemberDto(Guid.Parse(reader.GetString(0)), cohortId, quest.Quest.Id, Guid.Parse(reader.GetString(1)),
            reader.GetInt64(2), (GuildCohortMemberState)reader.GetInt32(3),
            closed is { } version && reader.GetInt64(2) <= version && (removed is null || removed > version), removed, reader.GetInt64(6));
    }
}
