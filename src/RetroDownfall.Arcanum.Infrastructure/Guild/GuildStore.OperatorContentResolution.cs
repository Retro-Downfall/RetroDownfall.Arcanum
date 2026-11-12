using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    internal Task<Result<GuildContentResolutionDto>> ResolveOperatorContentAsync(GuildLocalHostPolicy policy,
        GuildQuestStore quests, GuildCallerContext caller, string? prefix, CancellationToken ct)
    {
        if (!IsContentPrefix(prefix))
            return Task.FromResult(Result<GuildContentResolutionDto>.Failure(new Error(ErrorCodes.Guild.InvalidRequest,
                "A canonical UUID prefix is required.")));

        return ExecuteAsync<GuildContentResolutionDto>(false, async (connection, transaction, token) =>
        {
            if (caller.Kind != GuildCallerKind.Operator || !policy.Authenticates(caller)) return ContentMissing;

            Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

            if (located.IsFailure) return located.Error;

            // Two visible identities suffice to prove ambiguity. The predicate filters ownership
            // and private kinds in SQLite, before LIMIT; no body or unbounded catalog is allocated.
            // The exact metadata reader repeats each selected source binding in this same snapshot.
            List<(Guid Id, long Revision)> identities = new(2);

            await using (SqliteCommand query = Command(connection, transaction, """
                WITH content AS NOT MATERIALIZED (
                    SELECT ContentId,ContentKindCode,SourceRecordId,Revision,SessionId,CampaignId
                    FROM guild_content WHERE ContentId GLOB $prefix
                    UNION ALL
                    SELECT ContentId,ContentKindCode,SourceRecordId,Revision,SessionId,CampaignId
                    FROM guild_content_erasure_receipts WHERE ContentId GLOB $prefix
                ), bound AS NOT MATERIALIZED (
                    SELECT c.*,CASE c.ContentKindCode
                        WHEN 1 THEN (SELECT q.Id FROM guild_quests q WHERE q.Id=c.SourceRecordId AND q.GoalContentId=c.ContentId)
                        WHEN 2 THEN (SELECT r.QuestId FROM guild_result_receipts r WHERE r.ResultId=c.SourceRecordId AND r.ContentId=c.ContentId)
                        WHEN 3 THEN coalesce(
                            (SELECT e.QuestId FROM guild_envelopes e WHERE e.Id=c.SourceRecordId AND e.ContentId=c.ContentId),
                            (SELECT a.QuestId FROM guild_continuations a WHERE a.ContinuationId=c.SourceRecordId AND a.ContentId=c.ContentId))
                        WHEN 5 THEN (SELECT a.PrimaryVotingQuestId FROM guild_cohort_contributions a WHERE a.ResultId=c.SourceRecordId AND a.ContentId=c.ContentId)
                        WHEN 6 THEN (SELECT o.IssuerQuestId FROM guild_work_offers o WHERE o.Id=c.SourceRecordId AND o.GoalContentId=c.ContentId)
                        WHEN 7 THEN (SELECT b.AuthorQuestId FROM guild_work_bids b WHERE b.Id=c.SourceRecordId AND b.NoteContentId=c.ContentId)
                        WHEN 8 THEN (SELECT coalesce(l.AuthorQuestId,h.IssuerQuestId) FROM guild_cohort_ledger l
                            JOIN guild_cohorts h ON h.Id=l.CohortId WHERE l.Id=c.SourceRecordId AND l.ContentId=c.ContentId AND h.Retired=0)
                        END AS OwnerQuestId
                    FROM content c WHERE c.ContentKindCode IN(0,1,2,3,5,6,7,8)
                )
                SELECT CASE WHEN typeof(c.ContentId)='text' AND length(CAST(c.ContentId AS BLOB))=36 THEN c.ContentId END,
                    CASE WHEN typeof(c.Revision)='integer' AND c.Revision>=1 THEN c.Revision END
                FROM bound c
                WHERE (c.SessionId IS NULL OR (EXISTS(SELECT 1 FROM Sessions s WHERE s.Id=c.SessionId AND s.CampaignId IS c.CampaignId)
                        AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=2 AND d.OwnerId=c.SessionId)
                        AND NOT EXISTS(SELECT 1 FROM guild_owner_cleanup d WHERE d.OwnerKindCode=2 AND d.OwnerId=c.SessionId)))
                    AND (c.CampaignId IS NULL OR (EXISTS(SELECT 1 FROM Campaigns p WHERE p.Id=c.CampaignId)
                        AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=1 AND d.OwnerId=c.CampaignId)
                        AND NOT EXISTS(SELECT 1 FROM guild_owner_cleanup d WHERE d.OwnerKindCode=1 AND d.OwnerId=c.CampaignId)))
                    AND ((c.ContentKindCode=0 AND EXISTS(
                        SELECT 1 FROM guild_agent_card_revisions r JOIN guild_agent_cards a ON a.CardId=r.CardId
                        WHERE r.CardId=c.SourceRecordId AND r.ContentId=c.ContentId AND r.Revision=c.Revision AND a.Available=1
                            AND a.SessionId IS c.SessionId AND a.CampaignId IS c.CampaignId
                            AND (a.WorkspaceId IS NULL OR EXISTS(SELECT 1 FROM Campaigns w WHERE w.Id=a.WorkspaceId))
                            AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=1 AND d.OwnerId=a.WorkspaceId)))
                        OR (c.ContentKindCode<>0 AND EXISTS(
                            SELECT 1 FROM guild_quests q JOIN guild_signets s ON s.QuestId=q.Id
                            WHERE q.Id=c.OwnerQuestId AND q.Retired=0 AND q.InstallationGeneration=$generation
                                AND q.SessionId IS c.SessionId AND q.CampaignId IS c.CampaignId AND s.OriginPrincipalId=$principal)))
                ORDER BY c.ContentId LIMIT 2
                """))
            {
                Add(query, "$prefix", prefix!.ToUpperInvariant() + "*");

                Add(query, "$generation", Format(caller.InstallationGeneration));

                Add(query, "$principal", Format(caller.PrincipalId!.Value));

                await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid id)
                        || id == Guid.Empty) return Unavailable();

                    identities.Add((id, reader.GetInt64(1)));
                }
            }

            ImmutableArray<GuildDataRef>.Builder candidates = ImmutableArray.CreateBuilder<GuildDataRef>(2);

            foreach ((Guid id, long revision) in identities)
            {
                Result<GuildDataRef> reference = await ReadOperatorContentReferenceWithinAsync(connection, transaction,
                    policy, quests, caller, id, revision, token).ConfigureAwait(false);

                // An inconsistent selected source cannot establish absence or uniqueness. Refuse
                // the lookup rather than silently removing it and selecting a different identity.
                if (reference.IsFailure) return reference.Error;

                candidates.Add(reference.Value);
            }

            return new GuildContentResolutionDto(candidates.ToImmutable());
        }, ct);
    }

    private static bool IsContentPrefix(string? prefix)
    {
        if (prefix is not { Length: > 0 and <= 36 }) return false;

        for (int index = 0; index < prefix.Length; index++)
        {
            if (index is 8 or 13 or 18 or 23)
            {
                if (prefix[index] != '-') return false;
            }
            else if (prefix[index] is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        }

        return true;
    }
}
