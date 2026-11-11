using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private static bool MatchesCohortToolIntent(GuildChildToolAdmission admission, GuildPublishRequest request)
    {
        JsonElement args = admission.Arguments;

        if (!GuildTaskArguments.Closed(args, "cohortId", "kind", "contentType", "body", "idempotencyKey")
            || args.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != args.EnumerateObject().Count()
            || !GuildTaskArguments.Identity(args, "cohortId", out Guid cohort) || cohort != request.Address.CohortId
            || !GuildTaskArguments.Text(args, "kind", out string? kind) || kind != "message"
            || !GuildTaskArguments.Text(args, "contentType", out string? mime) || mime != request.MimeType
            || !GuildTaskArguments.Text(args, "body", out string? body) || body is null) return false;

        try
        {
            if (GuildQuestIntent.NormalizeGoal(body) != request.Body) return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        Result<Guid> mutation = GuildTaskArguments.Mutation(args, admission.LogicalCallId);

        return mutation.IsSuccess && mutation.Value == request.IdempotencyKey && request.Kind == GuildEnvelopeKind.Progress
            && request.ExpectedRevision is null;
    }

    private static async Task<Result<long>> AuthorizeCohortPublicationWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildQuestStored source, Guid cohortId, CancellationToken ct)
    {
        if (caller.Kind != GuildCallerKind.Run) return Forbidden;

        Result<long> membership = await ReadCohortSourceVersionWithinAsync(connection, transaction, source, cohortId, ct).ConfigureAwait(false);

        if (membership.IsFailure) return membership.Error;

        long version = membership.Value;

        string after = "";

        while (true)
        {
            List<Guid> page = [];

            await using (SqliteCommand recipients = Command(connection, transaction, """
                SELECT a.QuestId FROM guild_cohort_members m
                JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
                JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=$root
                WHERE m.CohortId=$cohort AND m.StateCode=1 AND a.QuestId>$after
                  AND m.Id<>(SELECT s.MemberId FROM guild_cohort_member_quests s
                    JOIN guild_cohort_members sm ON sm.Id=s.MemberId
                    WHERE s.QuestId=$source AND s.Active=1 AND sm.CohortId=$cohort AND sm.StateCode=1)
                ORDER BY a.QuestId LIMIT 100
                """))
            {
                Add(recipients, "$cohort", Id(cohortId));

                Add(recipients, "$root", Id(source.Quest.RootQuestId));

                Add(recipients, "$source", Id(source.Quest.Id));

                Add(recipients, "$after", after);

                await using SqliteDataReader reader = await recipients.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false)) page.Add(Guid.Parse(reader.GetString(0)));
            }

            if (page.Count == 0) break;

            foreach (Guid quest in page)
            {
                Result<GuildQuestStored> target = await GuildQuestStore.ReadWithinAsync(connection, transaction, quest, ct).ConfigureAwait(false);

                if (target.IsFailure) return target.Error;

                if (target.Value.Retired || target.Value.Quest.RootQuestId != source.Quest.RootQuestId) return Forbidden;

                Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, target.Value.Quest.SessionId,
                    target.Value.Quest.CampaignId, ct).ConfigureAwait(false);

                if (owner.IsFailure) return owner.Error;

                Result<GuildGrant> grant = await signets.ValidateCurrentGrantAsync(quest, ct).ConfigureAwait(false);

                if (grant.IsFailure) return grant.Error;
            }

            after = Id(page[^1]);
        }

        return version;
    }

    private static async Task<Result<long>> ReadCohortSourceVersionWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored source, Guid cohortId, CancellationToken ct)
    {
        await using SqliteCommand member = Command(connection, transaction, """
            SELECT c.MembershipVersion FROM guild_cohorts c
            JOIN guild_cohort_members m ON m.CohortId=c.Id AND m.StateCode=1
            JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
            JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
            WHERE c.Id=$cohort AND c.RootQuestId=$root AND c.StateCode IN (1,2)
              AND a.QuestId=$source AND c.MembershipVersion>=1
            """);

        Add(member, "$cohort", Id(cohortId));

        Add(member, "$root", Id(source.Quest.RootQuestId));

        Add(member, "$source", Id(source.Quest.Id));

        if (await member.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long version) return Forbidden;

        return version;
    }

    private static async Task<Result> InsertCohortAudienceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid envelope, Guid stream, long sequence, Guid source, Guid cohort, DateTimeOffset now, CancellationToken ct)
    {
        await using SqliteCommand audience = Command(connection, transaction, """
            INSERT INTO guild_portal_cohort_audience(EnvelopeId,MemberId,QuestId)
            SELECT $envelope,m.Id,a.QuestId FROM guild_cohort_members m
            JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
            JOIN guild_cohorts c ON c.Id=m.CohortId AND c.StateCode IN (1,2)
            JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
            WHERE m.CohortId=$cohort AND m.StateCode=1
              AND m.Id<>(SELECT s.MemberId FROM guild_cohort_member_quests s JOIN guild_cohort_members sm ON sm.Id=s.MemberId
                  WHERE s.QuestId=$source AND s.Active=1 AND sm.CohortId=$cohort AND sm.StateCode=1);
            INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
            SELECT upper(hex(randomblob(4))||'-'||hex(randomblob(2))||'-'||hex(randomblob(2))||'-'||hex(randomblob(2))||'-'||hex(randomblob(6))),
                $envelope,'quest:'||QuestId,QuestId,$stream,$sequence,1,1,$now
            FROM guild_portal_cohort_audience WHERE EnvelopeId=$envelope;
            """);

        Add(audience, "$envelope", Id(envelope));

        Add(audience, "$cohort", Id(cohort));

        Add(audience, "$source", Id(source));

        Add(audience, "$stream", Id(stream));

        Add(audience, "$sequence", sequence);

        Add(audience, "$now", UtcInstantText.Format(now));

        _ = await audience.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    private static async Task<Result> InsertDirectInboxWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid envelope, Guid stream, long sequence, Recipient recipient, DateTimeOffset now, CancellationToken ct)
    {
        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
            VALUES($inbox,$envelope,$recipient,$quest,$stream,$sequence,1,1,$now)
            """);

        Add(insert, "$inbox", Id(Guid.NewGuid()));

        Add(insert, "$envelope", Id(envelope));

        Add(insert, "$recipient", recipient.Key);

        Add(insert, "$quest", Id(recipient.QuestId));

        Add(insert, "$stream", Id(stream));

        Add(insert, "$sequence", sequence);

        Add(insert, "$now", UtcInstantText.Format(now));

        return await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Unavailable;
    }

    private static async Task<bool> HasCurrentCohortAudienceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildEnvelope envelope, Guid recipient, CancellationToken ct)
    {
        if (envelope.Producer.Kind != GuildProducerKind.Run || envelope.Authority != GuildAuthorityKind.Peer
            || envelope.Address.CohortId is not { } cohort || envelope.Address.QuestId is not null
            || envelope.Address.MembershipVersion is not > 0) return false;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM guild_cohorts c
            JOIN guild_cohort_members sender ON sender.CohortId=c.Id AND sender.StateCode=1
            JOIN guild_cohort_member_quests sa ON sa.MemberId=sender.Id AND sa.Active=1 AND sa.QuestId=$source
            JOIN guild_quests sq ON sq.Id=sa.QuestId AND sq.AgentId=sender.AgentId AND sq.RootQuestId=c.RootQuestId
            JOIN guild_portal_cohort_audience frozen ON frozen.EnvelopeId=$envelope AND frozen.QuestId=$recipient
            JOIN guild_cohort_members target ON target.Id=frozen.MemberId AND target.CohortId=c.Id AND target.StateCode=1
            JOIN guild_cohort_member_quests ta ON ta.MemberId=target.Id AND ta.QuestId=frozen.QuestId AND ta.Active=1
            JOIN guild_quests tq ON tq.Id=ta.QuestId AND tq.AgentId=target.AgentId AND tq.RootQuestId=c.RootQuestId
            WHERE c.Id=$cohort AND c.RootQuestId=$root AND c.StateCode IN (1,2)
              AND c.MembershipVersion >= $version AND sender.Id<>target.Id
            """);

        Add(command, "$source", Id(envelope.QuestId));

        Add(command, "$envelope", Id(envelope.Id));

        Add(command, "$recipient", Id(recipient));

        Add(command, "$cohort", Id(cohort));

        Add(command, "$root", Id(envelope.RootQuestId));

        Add(command, "$version", envelope.Address.MembershipVersion);

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task<Result> RequireCohortEventScopeWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, Guid root, Guid cohort, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM guild_cohorts c WHERE c.Id=$cohort AND c.RootQuestId=$root AND c.StateCode IN (1,2)
              AND ($operator=1 OR c.IssuerQuestId=$quest OR EXISTS(SELECT 1 FROM guild_cohort_members m
                JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
                JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
                WHERE m.CohortId=c.Id AND m.StateCode=1 AND a.QuestId=$quest))
            """);

        Add(command, "$cohort", Id(cohort));

        Add(command, "$root", Id(root));

        Add(command, "$operator", caller.Kind == GuildCallerKind.Operator ? 1 : 0);

        Add(command, "$quest", Id(caller.Execution?.QuestId));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null ? Result.Success() : Forbidden;
    }

    private static async Task<bool> IsCurrentCohortQuestWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, Guid root, Guid quest, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM guild_cohorts c JOIN guild_cohort_members m ON m.CohortId=c.Id AND m.StateCode=1
            JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
            JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
            WHERE c.Id=$cohort AND c.RootQuestId=$root AND c.StateCode IN (1,2) AND a.QuestId=$quest
            """);

        Add(command, "$cohort", Id(cohort));

        Add(command, "$root", Id(root));

        Add(command, "$quest", Id(quest));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> IsCohortEventActorAuthorizedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        PortalEvent row, Guid root, Guid actor, CancellationToken ct)
    {
        if (row.Kind is GuildEventKind.OfferChanged or GuildEventKind.BidChanged
            or GuildEventKind.AwardCommitted or GuildEventKind.NoteAppended) return false;

        if (row.Kind is not (GuildEventKind.CohortChanged or GuildEventKind.MembershipChanged or GuildEventKind.ContributionAccepted)) return true;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM guild_cohorts c WHERE c.Id=$cohort AND c.RootQuestId=$root
              AND (c.IssuerQuestId=$actor OR (c.StateCode IN(1,2) AND EXISTS(
                SELECT 1 FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
                JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=c.RootQuestId
                WHERE m.CohortId=c.Id AND m.StateCode=1 AND a.QuestId=$actor)))
            """);

        Add(command, "$cohort", Id(row.ObjectId));
        Add(command, "$root", Id(root));
        Add(command, "$actor", Id(actor));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

}
