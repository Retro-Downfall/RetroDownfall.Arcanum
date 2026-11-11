using RetroDownfall.Arcanum.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private static readonly Error MarketProtectedContent = new(ErrorCodes.Guild.AuthorityUnavailable, "The work content requires a qualified disclosure boundary.");

    // These helpers require the enclosing transaction to authorize the current reader or mutation.
    internal static async Task<Result<WorkOfferDto>> ReadOfferWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid offerId, CancellationToken ct)
    {
        Guid cohort;
        Guid issuer;
        Guid content;
        long sequence;
        long revision;
        string skill;
        string mime;
        GuildWorkOfferState state;
        Guid? award;
        Guid? quest;

        await using (var query = Command(connection, transaction, """
            SELECT CohortId,IssuerQuestId,GoalContentId,OfferSequence,Revision,SkillId,MimeType,StateCode,AwardId,QuestId
            FROM guild_work_offers WHERE Id=$id
                AND typeof(CohortId)='text' AND length(CAST(CohortId AS BLOB))=36
                AND typeof(IssuerQuestId)='text' AND length(CAST(IssuerQuestId AS BLOB))=36
                AND typeof(GoalContentId)='text' AND length(CAST(GoalContentId AS BLOB))=36
                AND typeof(OfferSequence)='integer' AND OfferSequence>=1 AND typeof(Revision)='integer' AND Revision>=1
                AND typeof(SkillId)='text' AND length(CAST(SkillId AS BLOB)) BETWEEN 1 AND 65536
                AND MimeType IN('text/plain','application/json') AND typeof(StateCode)='integer' AND StateCode IN(1,2,3,4)
                AND (AwardId IS NULL OR typeof(AwardId)='text' AND length(CAST(AwardId AS BLOB))=36)
                AND (QuestId IS NULL OR typeof(QuestId)='text' AND length(CAST(QuestId AS BLOB))=36)
            """))
        {
            Add(query, "$id", Id(offerId));

            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return MarketNotFound;

            try
            {
                cohort = Guid.ParseExact(reader.GetString(0), "D");
                issuer = Guid.ParseExact(reader.GetString(1), "D");
                content = Guid.ParseExact(reader.GetString(2), "D");
                sequence = reader.GetInt64(3);
                revision = reader.GetInt64(4);
                skill = reader.GetString(5);
                mime = reader.GetString(6);
                state = (GuildWorkOfferState)reader.GetInt32(7);
                award = reader.IsDBNull(8) ? null : Guid.ParseExact(reader.GetString(8), "D");
                quest = reader.IsDBNull(9) ? null : Guid.ParseExact(reader.GetString(9), "D");
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return Unavailable; }
        }

        if (cohort == Guid.Empty || issuer == Guid.Empty || content == Guid.Empty || award == Guid.Empty || quest == Guid.Empty
            || (state == GuildWorkOfferState.Awarded ? award is null || quest is null : award is not null || quest is not null)) return Unavailable;

        var reference = await ReadMarketContentWithinAsync(connection, transaction, issuer, offerId, content, GuildContentKind.Offer, ct).ConfigureAwait(false);

        return reference.IsFailure ? reference.Error : new WorkOfferDto(offerId, cohort, issuer, sequence, revision,
            skill, new(mime), reference.Value, state, award, quest);
    }

    internal static async Task<Result<WorkBidDto>> ReadBidWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid bidId, CancellationToken ct)
    {
        Guid offer;
        Guid member;
        Guid agent;
        Guid author;
        Guid content;
        long cardRevision;
        long sequence;
        long revision;
        GuildWorkBidState state;
        decimal? cost;

        await using (var query = Command(connection, transaction, """
            SELECT OfferId,MemberId,AgentId,AuthorQuestId,NoteContentId,CardRevision,BidSequence,Revision,StateCode,ClaimedCostUsd
            FROM guild_work_bids WHERE Id=$id
                AND typeof(OfferId)='text' AND length(CAST(OfferId AS BLOB))=36
                AND typeof(MemberId)='text' AND length(CAST(MemberId AS BLOB))=36
                AND typeof(AgentId)='text' AND length(CAST(AgentId AS BLOB))=36
                AND typeof(AuthorQuestId)='text' AND length(CAST(AuthorQuestId AS BLOB))=36
                AND typeof(NoteContentId)='text' AND length(CAST(NoteContentId AS BLOB))=36
                AND typeof(CardRevision)='integer' AND CardRevision>=1 AND typeof(BidSequence)='integer' AND BidSequence>=1
                AND typeof(Revision)='integer' AND Revision>=1 AND typeof(StateCode)='integer' AND StateCode IN(1,2)
                AND (ClaimedCostUsd IS NULL OR typeof(ClaimedCostUsd)='text' AND length(CAST(ClaimedCostUsd AS BLOB)) BETWEEN 1 AND 64)
            """))
        {
            Add(query, "$id", Id(bidId));

            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return MarketNotFound;

            try
            {
                offer = Guid.ParseExact(reader.GetString(0), "D");
                member = Guid.ParseExact(reader.GetString(1), "D");
                agent = Guid.ParseExact(reader.GetString(2), "D");
                author = Guid.ParseExact(reader.GetString(3), "D");
                content = Guid.ParseExact(reader.GetString(4), "D");
                cardRevision = reader.GetInt64(5);
                sequence = reader.GetInt64(6);
                revision = reader.GetInt64(7);
                state = (GuildWorkBidState)reader.GetInt32(8);
                cost = reader.IsDBNull(9) ? null : ExactUsdText.Parse(reader.GetString(9));
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException) { return Unavailable; }
        }

        if (offer == Guid.Empty || member == Guid.Empty || agent == Guid.Empty || author == Guid.Empty || content == Guid.Empty || cost < 0) return Unavailable;

        await using (var relation = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_work_offer_recipients r JOIN guild_cohort_member_quests a ON a.MemberId=r.MemberId
                JOIN guild_quests q ON q.Id=a.QuestId
                WHERE r.OfferId=$offer AND r.MemberId=$member AND r.AgentId=$agent AND r.CardRevision=$revision
                    AND a.QuestId=$author AND q.AgentId=$agent)
            """))
        {
            Add(relation, "$offer", Id(offer));
            Add(relation, "$member", Id(member));
            Add(relation, "$agent", Id(agent));
            Add(relation, "$revision", cardRevision);
            Add(relation, "$author", Id(author));

            if (!Equals(1L, await relation.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Unavailable;
        }

        var reference = await ReadMarketContentWithinAsync(connection, transaction, author, bidId, content, GuildContentKind.Bid, ct).ConfigureAwait(false);

        return reference.IsFailure ? reference.Error : new WorkBidDto(bidId, offer, member, agent, cardRevision, sequence,
            revision, state, reference.Value, cost);
    }

    private static async Task<Result<GuildDataRef>> ReadMarketContentWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid author, Guid record, Guid content, GuildContentKind kind, CancellationToken ct)
    {
        var source = await GuildQuestStore.ReadWithinAsync(connection, transaction, author, ct).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        if (source.Value.Retired) return new Error(ErrorCodes.Guild.OwnerUnavailable, "The work content owner is unavailable.");

        var owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, source.Value.Quest.SessionId,
            source.Value.Quest.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure) return owner.Error;

        byte[] digest;
        long bytes;
        bool erased;

        await using (var metadata = Command(connection, transaction, """
            SELECT Digest,Bytes,Erased FROM (
                SELECT Digest,length(Body) AS Bytes,0 AS Erased FROM guild_content
                WHERE ContentId=$content AND SourceRecordId=$record AND ContentKindCode=$kind AND Revision=1 AND MimeType='text/plain'
                    AND typeof(Body)='blob' AND length(Body) BETWEEN $minimum AND 65536 AND typeof(Digest)='blob' AND length(Digest)=32
                UNION ALL
                SELECT Digest,ByteLength,1 FROM guild_content_erasure_receipts
                WHERE ContentId=$content AND SourceRecordId=$record AND ContentKindCode=$kind AND Revision=1 AND MimeType='text/plain'
                    AND typeof(ByteLength)='integer' AND ByteLength BETWEEN $minimum AND 65536 AND typeof(Digest)='blob' AND length(Digest)=32
            )
            """))
        {
            Add(metadata, "$content", Id(content));
            Add(metadata, "$record", Id(record));
            Add(metadata, "$kind", (int)kind);
            Add(metadata, "$minimum", kind == GuildContentKind.Bid ? 0 : 1);

            await using var reader = await metadata.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

            digest = (byte[])reader.GetValue(0);
            bytes = reader.GetInt64(1);
            erased = reader.GetInt64(2) == 1;

            if (await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;
        }

        var checkedContent = await ReadCurrentContentWithinAsync(connection, transaction, record, source.Value.Quest.SessionId,
            source.Value.Quest.CampaignId, content, kind, 1, "text/plain", bytes, digest, null, null, ct, true).ConfigureAwait(false);

        if (checkedContent.IsFailure) return checkedContent.Error;

        if (checkedContent.Value is { } reference) return reference;

        return erased ? new GuildDataRef(content, kind, 1, Convert.ToHexString(digest), "text/plain", bytes, true) : MarketProtectedContent;
    }
}
