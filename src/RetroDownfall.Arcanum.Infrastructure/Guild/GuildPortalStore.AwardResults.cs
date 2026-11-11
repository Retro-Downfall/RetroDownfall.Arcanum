using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private sealed record AwardResultPublication(Guid Award, Guid Cohort, Guid Member, Guid Primary, Guid Result);

    internal async Task<Result> PublishAwardResultWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, Guid workQuestId, DateTimeOffset now, CancellationToken ct)
    {
        var proof = await ReadAwardResultPublicationWithinAsync(connection, transaction, workQuestId, ct).ConfigureAwait(false);

        if (proof.IsFailure) return proof.Error;

        if (proof.Value is not { } binding) return Result.Success();

        var source = await GuildQuestStore.ReadWithinAsync(connection, transaction, workQuestId, ct).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        var cohort = await GuildCohortStore.ReadWithinAsync(connection, transaction, binding.Cohort, ct).ConfigureAwait(false);

        if (cohort.IsFailure) return cohort.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? Result.Success() : cohort.Error;

        if (source.Value.Retired || cohort.Value.Cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed)) return Result.Success();

        var primary = await GuildQuestStore.ReadWithinAsync(connection, transaction, binding.Primary, ct).ConfigureAwait(false);

        if (primary.IsFailure) return primary.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? Result.Success() : primary.Error;

        if (primary.Value.Retired || primary.Value.Quest.CancellationRequested
            || primary.Value.Quest.State is not (QuestState.Submitted or QuestState.Working or QuestState.InputRequired)) return Result.Success();

        foreach (Guid quest in new[] { workQuestId, binding.Primary })
        {
            var live = await signets.ValidateCurrentGrantAsync(quest, ct).ConfigureAwait(false);

            if (live.IsFailure) return live.Error.Code is ErrorCodes.Guild.AuthorityRevoked or ErrorCodes.Guild.AuthorityExpired
                or ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.Forbidden ? Result.Success() : live.Error;
        }

        GuildCallerContext receipt = new(GuildCallerKind.HostReceipt, workQuestId, binding.Result, policy.InstallationGeneration,
            receiptId: binding.Result, receiptSource: "result");

        return await PublishDistributionWithinAsync(connection, transaction, signets, receipt, source.Value, cohort.Value.Cohort,
            new("work.result", binding.Cohort, workQuestId, 1), Guid.NewGuid(), binding.Member, now, ct, binding.Primary).ConfigureAwait(false);
    }

    private static async Task<Result<AwardResultPublication?>> ReadAwardResultPublicationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using var query = Command(connection, transaction, """
            SELECT CASE WHEN length(CAST(a.Id AS BLOB))=36 THEN a.Id END,
                CASE WHEN length(CAST(m.CohortId AS BLOB))=36 THEN m.CohortId END,
                CASE WHEN length(CAST(m.Id AS BLOB))=36 THEN m.Id END,
                CASE WHEN length(CAST(m.PrimaryVotingQuestId AS BLOB))=36 THEN m.PrimaryVotingQuestId END,
                CASE WHEN length(CAST(r.ResultId AS BLOB))=36 THEN r.ResultId END
            FROM guild_work_awards a JOIN guild_cohort_members m ON m.Id=a.MemberId AND m.PrimaryVotingQuestId=a.PrimaryQuestId
            JOIN guild_cohort_member_quests edge ON edge.MemberId=m.Id AND edge.QuestId=a.QuestId AND edge.AssociationKindCode=2
            JOIN guild_result_receipts r ON r.QuestId=a.QuestId AND r.OutcomeCode IN(3,4,5,6)
            WHERE a.QuestId=$quest AND m.StateCode=1 AND edge.Active=1
            """);

        Add(query, "$quest", Id(questId));

        await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<AwardResultPublication?>.Success(null);

        Guid[] ids = new Guid[5];

        for (int index = 0; index < ids.Length; index++)
            if (reader.IsDBNull(index) || !Guid.TryParseExact(reader.GetString(index), "D", out ids[index]) || ids[index] == Guid.Empty) return Unavailable;

        if (await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

        return new AwardResultPublication(ids[0], ids[1], ids[2], ids[3], ids[4]);
    }

    private static async Task<bool> HasCurrentAwardResultAudienceWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildEnvelope envelope, Guid recipient, CancellationToken ct)
    {
        if (envelope.Producer.Kind != GuildProducerKind.HostReceipt || envelope.Producer.SourceKind != GuildReceiptSourceKind.Result
            || envelope.Authority != GuildAuthorityKind.Peer || envelope.Address.CohortId is not { } cohort
            || envelope.Address.Kind != GuildCircleAddressKind.Cohort || envelope.Address.QuestId is not null) return false;

        await using var query = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_distribution_notifications n
                JOIN guild_work_awards award ON award.QuestId=n.ObjectId AND award.Id=$award
                JOIN guild_result_receipts result ON result.QuestId=award.QuestId AND result.ResultId=$result
                JOIN guild_cohort_members member ON member.Id=award.MemberId AND member.CohortId=n.CohortId
                    AND member.PrimaryVotingQuestId=award.PrimaryQuestId AND member.StateCode=1
                JOIN guild_cohorts c ON c.Id=member.CohortId AND c.StateCode IN(1,2) AND c.RootQuestId=$root
                JOIN guild_cohort_member_quests work ON work.MemberId=member.Id AND work.QuestId=award.QuestId
                    AND work.AssociationKindCode=2 AND work.Active=1
                JOIN guild_cohort_member_quests primaryEdge ON primaryEdge.MemberId=member.Id AND primaryEdge.QuestId=award.PrimaryQuestId
                    AND primaryEdge.AssociationKindCode=1 AND primaryEdge.Active=1
                JOIN guild_portal_cohort_audience audience ON audience.EnvelopeId=n.EnvelopeId AND audience.MemberId=member.Id
                    AND audience.QuestId=award.PrimaryQuestId
                WHERE n.EnvelopeId=$envelope AND n.TypeCode=7 AND n.CohortId=$cohort AND n.ObjectId=$source
                    AND n.ObjectSequence=1 AND award.PrimaryQuestId=$recipient AND c.MembershipVersion>=$version)
            """);

        Add(query, "$award", Id(envelope.Producer.DomainOwnerId));
        Add(query, "$result", Id(envelope.Producer.ReceiptId));
        Add(query, "$root", Id(envelope.RootQuestId));
        Add(query, "$envelope", Id(envelope.Id));
        Add(query, "$cohort", Id(cohort));
        Add(query, "$source", Id(envelope.QuestId));
        Add(query, "$recipient", Id(recipient));
        Add(query, "$version", envelope.Address.MembershipVersion);

        return Equals(1L, await query.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
