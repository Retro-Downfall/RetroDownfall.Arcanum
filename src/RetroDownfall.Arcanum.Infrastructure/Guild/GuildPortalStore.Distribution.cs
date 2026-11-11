using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    /// <summary>Only committed domain mutations call this with host-created content-free references.</summary>
    internal async Task<Result> PublishDistributionWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildQuestStored source, CohortDto cohort,
        GuildCohortProgress notice, Guid publicationId, Guid? recipientMemberId, DateTimeOffset now, CancellationToken ct, Guid? recipientQuestId = null)
    {
        if (notice.Type is not ("offer.posted" or "bid.submitted" or "bid.withdrawn" or "offer.withdrawn" or "work.awarded" or "note.appended" or "work.result")
            || notice.CohortId != cohort.Id || notice.ObjectId == Guid.Empty || notice.Sequence < 1 || publicationId == Guid.Empty
            || source.Quest.RootQuestId != cohort.RootQuestId || caller.InstallationGeneration != policy.InstallationGeneration)
            return Forbidden;

        bool hostResult = caller.Kind == GuildCallerKind.HostReceipt && notice.Type == "work.result";
        if (notice.Type == "work.result" && !hostResult) return Forbidden;

        GuildProducerIdentity producer;
        GuildAuthorityKind relationship;

        if (hostResult)
        {
            var proof = await ReadAwardResultPublicationWithinAsync(connection, transaction, source.Quest.Id, ct).ConfigureAwait(false);

            if (proof.IsFailure) return proof.Error;

            if (proof.Value is not { } receipt || caller.PrincipalId != source.Quest.Id || caller.ReceiptId != receipt.Result
                || caller.ReceiptSource != "result" || notice.ObjectId != source.Quest.Id || notice.Sequence != 1
                || receipt.Cohort != cohort.Id || recipientMemberId != receipt.Member || recipientQuestId != receipt.Primary) return Forbidden;

            await using var replay = Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM guild_envelopes e JOIN guild_distribution_notifications n ON n.EnvelopeId=e.Id
                    WHERE e.QuestId=$quest AND e.ProducerRequestId=$receipt AND n.TypeCode=7 AND n.ObjectId=$quest)
                """);

            Add(replay, "$quest", Id(source.Quest.Id));
            Add(replay, "$receipt", Id(receipt.Result));

            if (Equals(1L, await replay.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Result.Success();

            producer = new(GuildProducerKind.HostReceipt, null, null, null, null, receipt.Award, GuildReceiptSourceKind.Result, receipt.Result);
            relationship = GuildAuthorityKind.Peer;
        }
        else
        {
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, source, ct).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            if (caller.Kind == GuildCallerKind.Run && caller.Execution is { } execution && execution.QuestId == source.Quest.Id)
            {
                var live = await signets.VerifyAsync(execution, ct).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                if (source.Quest.Id != cohort.IssuerQuestId)
                {
                    await using var member = Command(connection, transaction, """
                        SELECT EXISTS(SELECT 1 FROM guild_cohort_member_quests a JOIN guild_cohort_members m ON m.Id=a.MemberId
                            WHERE a.QuestId=$quest AND a.Active=1 AND m.CohortId=$cohort AND m.StateCode=1 AND m.AgentId=$agent)
                        """);

                    Add(member, "$quest", Id(source.Quest.Id));
                    Add(member, "$cohort", Id(cohort.Id));
                    Add(member, "$agent", Id(source.Quest.AgentId));

                    if (!Equals(1L, await member.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Forbidden;
                }

                producer = new(GuildProducerKind.Run, execution.AgentId, execution.RunId, execution.QuestId, null, null, null, null);
                relationship = source.Quest.Id == cohort.IssuerQuestId ? GuildAuthorityKind.Parent : GuildAuthorityKind.Peer;
            }
            else if (caller.Kind == GuildCallerKind.Operator && caller.PrincipalId is { } principal)
            {
                producer = new(GuildProducerKind.Operator, null, null, null, Id(principal), null, null, null);
                relationship = GuildAuthorityKind.Operator;
            }
            else return Forbidden;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(notice, GuildJsonContext.Default.GuildCohortProgress);
        Guid contentId = Guid.NewGuid();

        var label = await GuildQuestStore.ResultSensitivityWithinAsync(connection, transaction, source, contentId, bytes, null, ct).ConfigureAwait(false);

        if (label.IsFailure) return label.Error;

        var content = await GuildStore.WriteContentWithinAsync(connection, transaction,
            new(contentId, publicationId, GuildContentKind.CircleBody, 1, source.Quest.SessionId, source.Quest.CampaignId,
                "application/json", bytes, label.Value), now, ct).ConfigureAwait(false);

        if (content.IsFailure) return content.Error;

        string producerKey = ProducerKey(producer);
        Guid streamId = Guid.NewGuid();
        long sequence = 0;

        await using (var stream = Command(connection, transaction, "SELECT StreamId,LastSequence FROM guild_portal_streams WHERE QuestId=$quest AND ProducerKindCode=$kind AND ProducerKey=$key"))
        {
            Add(stream, "$quest", Id(source.Quest.Id));
            Add(stream, "$kind", (int)producer.Kind);
            Add(stream, "$key", producerKey);

            await using var reader = await stream.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                streamId = Guid.Parse(reader.GetString(0));
                sequence = reader.GetInt64(1);
            }
        }

        if (sequence == long.MaxValue) return Exhausted;

        await using (var advance = Command(connection, transaction, """
            INSERT INTO guild_portal_streams(StreamId,QuestId,ProducerKindCode,ProducerKey,LastSequence)
            VALUES($stream,$quest,$kind,$key,1)
            ON CONFLICT(QuestId,ProducerKindCode,ProducerKey) DO UPDATE SET LastSequence=LastSequence+1
            WHERE LastSequence=$prior AND LastSequence<9223372036854775807
            """))
        {
            Add(advance, "$stream", Id(streamId));
            Add(advance, "$quest", Id(source.Quest.Id));
            Add(advance, "$kind", (int)producer.Kind);
            Add(advance, "$key", producerKey);
            Add(advance, "$prior", sequence);

            if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Exhausted;
        }

        GuildEnvelope envelope = new(publicationId, producer, source.Quest.Id, cohort.RootQuestId,
            caller.Kind == GuildCallerKind.Run ? source.Quest.SignetRevision : null, source.Quest.SessionId, source.Quest.CampaignId,
            relationship, GuildEnvelopeKind.Progress, content.Value, sequence + 1, now,
            new(GuildCircleAddressKind.Cohort, null, cohort.Id, cohort.MembershipVersion));

        await using (var insert = Command(connection, transaction, """
            INSERT INTO guild_envelopes(Id,StreamId,ProducerRequestId,IntentDigest,QuestId,RootQuestId,SessionId,CampaignId,KindCode,ContentId,SenderSequence,MetadataJson,CreatedAtUtc)
            VALUES($id,$stream,$request,$digest,$quest,$root,$session,$campaign,$kind,$content,$sequence,$metadata,$now)
            """))
        {
            Add(insert, "$id", Id(publicationId));
            Add(insert, "$stream", Id(streamId));
            Add(insert, "$request", Id(producer.Kind == GuildProducerKind.HostReceipt ? producer.ReceiptId!.Value : publicationId));
            Add(insert, "$digest", IntentDigest(new(publicationId, envelope.Address, GuildEnvelopeKind.Progress,
                "application/json", StrictUtf8.GetString(bytes), null)));
            Add(insert, "$quest", Id(source.Quest.Id));
            Add(insert, "$root", Id(cohort.RootQuestId));
            Add(insert, "$session", Id(source.Quest.SessionId));
            Add(insert, "$campaign", Id(source.Quest.CampaignId));
            Add(insert, "$kind", (int)GuildEnvelopeKind.Progress);
            Add(insert, "$content", Id(contentId));
            Add(insert, "$sequence", sequence + 1);
            Add(insert, "$metadata", JsonSerializer.Serialize(envelope, GuildJsonContext.Default.GuildEnvelope));
            Add(insert, "$now", UtcInstantText.Format(now));

            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var binding = Command(connection, transaction, """
            INSERT INTO guild_distribution_notifications(EnvelopeId,CohortId,TypeCode,ObjectId,ObjectSequence)
            VALUES($envelope,$cohort,$type,$object,$sequence)
            """))
        {
            Add(binding, "$envelope", Id(publicationId));
            Add(binding, "$cohort", Id(cohort.Id));
            Add(binding, "$type", DistributionTypeCode(notice.Type));
            Add(binding, "$object", Id(notice.ObjectId));
            Add(binding, "$sequence", notice.Sequence);

            await binding.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        string after = "";

        while (true)
        {
            List<(Guid Member, Guid Quest)> targets = [];

            await using (var page = Command(connection, transaction, """
                SELECT m.Id,a.QuestId FROM guild_cohort_members m
                JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1
                JOIN guild_quests q ON q.Id=a.QuestId AND q.AgentId=m.AgentId AND q.RootQuestId=$root
                WHERE m.CohortId=$cohort AND m.StateCode=1 AND ($member IS NULL OR m.Id=$member)
                    AND ($recipient IS NULL OR a.QuestId=$recipient) AND a.QuestId<>$source AND a.QuestId>$after
                    AND ($hostResult=1 OR NOT EXISTS(SELECT 1 FROM guild_cohort_member_quests own WHERE own.MemberId=m.Id AND own.QuestId=$source))
                    AND q.Retired=0 AND q.CancellationRequested=0
                    AND NOT EXISTS(SELECT 1 FROM guild_result_receipts terminal WHERE terminal.QuestId=q.Id)
                ORDER BY a.QuestId LIMIT 100
                """))
            {
                Add(page, "$root", Id(cohort.RootQuestId));
                Add(page, "$hostResult", hostResult ? 1 : 0);
                Add(page, "$cohort", Id(cohort.Id));
                Add(page, "$member", Id(recipientMemberId));
                Add(page, "$recipient", Id(recipientQuestId));
                Add(page, "$source", Id(source.Quest.Id));
                Add(page, "$after", after);

                await using var reader = await page.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid member) || !Guid.TryParseExact(reader.GetString(1), "D", out Guid quest)) return Unavailable;

                    targets.Add((member, quest));
                }
            }

            if (targets.Count == 0) break;

            foreach (var target in targets)
            {
                var eligible = await signets.ValidateCurrentGrantAsync(target.Quest, ct).ConfigureAwait(false);

                if (eligible.IsFailure)
                {
                    if (eligible.Error.Code is ErrorCodes.Guild.AuthorityRevoked or ErrorCodes.Guild.AuthorityExpired or ErrorCodes.Guild.Forbidden) continue;

                    return eligible.Error;
                }

                await using var audience = Command(connection, transaction,
                    "INSERT INTO guild_portal_cohort_audience(EnvelopeId,MemberId,QuestId) VALUES($envelope,$member,$quest)");

                Add(audience, "$envelope", Id(publicationId));
                Add(audience, "$member", Id(target.Member));
                Add(audience, "$quest", Id(target.Quest));

                await audience.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                var inbox = await InsertDirectInboxWithinAsync(connection, transaction, publicationId, streamId, sequence + 1,
                    new("quest:" + Id(target.Quest), target.Quest, envelope.Address), now, ct).ConfigureAwait(false);

                if (inbox.IsFailure) return inbox.Error;
            }

            after = Id(targets[^1].Quest);
        }

        if (cohort.IssuerQuestId != source.Quest.Id && (recipientQuestId == cohort.IssuerQuestId || recipientQuestId is null && recipientMemberId is null))
        {
            var creator = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, ct).ConfigureAwait(false);

            if (creator.IsFailure) return creator.Error;

            if (!creator.Value.Retired && !creator.Value.Quest.CancellationRequested
                && creator.Value.Quest.State is QuestState.Submitted or QuestState.Working or QuestState.InputRequired)
            {
                var eligible = await signets.ValidateCurrentGrantAsync(cohort.IssuerQuestId, ct).ConfigureAwait(false);

                if (eligible.IsFailure) return eligible.Error;

                var inbox = await InsertDirectInboxWithinAsync(connection, transaction, publicationId, streamId, sequence + 1,
                    new("quest:" + Id(cohort.IssuerQuestId), cohort.IssuerQuestId, envelope.Address), now, ct).ConfigureAwait(false);

                if (inbox.IsFailure) return inbox.Error;
            }
        }

        var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, source.Quest.Id,
            GuildEventKind.MessageCommitted, publicationId, 1, publicationId, now, ct, GuildPortalEventSource.Envelope).ConfigureAwait(false);

        return projected.IsFailure ? projected.Error : Result.Success();
    }
}
