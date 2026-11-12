using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildStore
{
    internal Task<Result<GuildContentDto>> ReadOperatorContentAsync(GuildLocalHostPolicy policy, GuildQuestStore quests,
        GuildCallerContext caller, Guid contentId, long revision, CancellationToken ct) =>
        ExecuteAsync<GuildContentDto>(false, async (connection, transaction, token) =>
        {
            Result<GuildDataRef> resolved = await ReadOperatorContentReferenceWithinAsync(connection, transaction,
                policy, quests, caller, contentId, revision, token).ConfigureAwait(false);

            if (resolved.IsFailure) return resolved.Error;

            GuildDataRef reference = resolved.Value;

            if (reference.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The requested Guild content revision was erased.");

            await using (SqliteCommand disclosure = Command(connection, transaction, """
                SELECT CASE WHEN c.SensitivityCode<>0 OR c.ProvenanceModeCode<>0
                        OR c.ExactGenerationIds IS NOT NULL OR c.GenerationBloom IS NOT NULL
                        OR EXISTS(SELECT 1 FROM artifact_sensitivity l WHERE l.ArtifactKindCode=17
                            AND lower(replace(l.ArtifactId,'-',''))=lower(replace(c.ContentId,'-','')))
                    THEN 1 ELSE 0 END
                FROM guild_content c WHERE c.ContentId=$id
                """))
            {
                Add(disclosure, "$id", Format(contentId));

                if (!Equals(0L, await disclosure.ExecuteScalarAsync(token).ConfigureAwait(false)))
                    return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Protected Guild content requires a qualified disclosure adapter.");
            }

            // This host's existing content adapter repeats exact metadata, digest and current
            // sensitivity checks before allocating bytes. Protected data requires its qualified
            // disclosure adapter and never inherits permission from native API authentication.
            Result<string> body = await GuildQuestStore.ReadProviderContentWithinAsync(connection, transaction, reference, token).ConfigureAwait(false);

            return body.IsFailure ? body.Error : new GuildContentDto(reference, body.Value);
        }, ct);

    private static async Task<Result<GuildDataRef>> ReadOperatorContentReferenceWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildLocalHostPolicy policy, GuildQuestStore quests, GuildCallerContext caller,
        Guid contentId, long revision, CancellationToken token)
    {
        if (contentId == Guid.Empty || revision < 1 || caller.Kind != GuildCallerKind.Operator || !policy.Authenticates(caller))
            return ContentMissing;

        Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, null, null, token).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        GuildDataRef reference;

        Guid sourceId;

        Guid? session;

        Guid? campaign;

        await using (SqliteCommand metadata = Command(connection, transaction, """
            WITH candidate AS (
                SELECT ContentKindCode,SourceRecordId,Revision,SessionId,CampaignId,Digest,MimeType,length(Body) AS Bytes,0 AS Erased
                FROM guild_content WHERE ContentId=$id AND Revision=$revision
                UNION ALL
                SELECT ContentKindCode,SourceRecordId,Revision,SessionId,CampaignId,Digest,MimeType,ByteLength,1
                FROM guild_content_erasure_receipts WHERE ContentId=$id AND Revision=$revision
            )
            SELECT CASE WHEN typeof(ContentKindCode)='integer' AND ContentKindCode BETWEEN 0 AND 10 THEN ContentKindCode END,
                CASE WHEN typeof(SourceRecordId)='text' AND length(CAST(SourceRecordId AS BLOB))=36 THEN SourceRecordId END,
                CASE WHEN SessionId IS NULL OR typeof(SessionId)='text' AND length(CAST(SessionId AS BLOB))=36 THEN SessionId END,
                CASE WHEN CampaignId IS NULL OR typeof(CampaignId)='text' AND length(CAST(CampaignId AS BLOB))=36 THEN CampaignId END,
                CASE WHEN typeof(Digest)='blob' AND length(Digest)=32 THEN Digest END,
                CASE WHEN MimeType IN('text/plain','application/json') THEN MimeType END,
                CASE WHEN typeof(Bytes)='integer' AND Bytes BETWEEN 0 AND 65536 THEN Bytes END,Erased,
                (SessionId IS NULL OR typeof(SessionId)='text' AND length(CAST(SessionId AS BLOB))=36)
                    AND (CampaignId IS NULL OR typeof(CampaignId)='text' AND length(CAST(CampaignId AS BLOB))=36)
            FROM candidate
            """))
        {
            Add(metadata, "$id", Format(contentId));

            Add(metadata, "$revision", revision);

            await using SqliteDataReader reader = await metadata.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return ContentMissing;

            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(4) || reader.IsDBNull(5)
                || reader.IsDBNull(6) || reader.GetInt64(8) != 1
                || !Guid.TryParseExact(reader.GetString(1), "D", out sourceId) || sourceId == Guid.Empty) return Unavailable();

            if (!TryContentOwner(reader, 2, out session) || !TryContentOwner(reader, 3, out campaign)) return Unavailable();

            reference = new(contentId, (GuildContentKind)reader.GetInt32(0), revision,
                Convert.ToHexString((byte[])reader.GetValue(4)), reader.GetString(5), reader.GetInt64(6), reader.GetInt64(7) != 0);

            if (await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable();
        }

        // Execution inputs and checkpoints contain private execution state, never public data
        // references. A guessed content id cannot turn the exact-body route into a plan reader.
        if (reference.Kind is GuildContentKind.ExecutionInput or GuildContentKind.ExecutionCheckpoint
            or GuildContentKind.CohortSpecification) return ContentMissing;

        Result owner = await RequireOwnerWithinAsync(connection, transaction, session, campaign, token).ConfigureAwait(false);

        if (owner.IsFailure) return ContentMissing;

        if (reference.Kind == GuildContentKind.AgentCard)
        {
            await using SqliteCommand card = Command(connection, transaction, """
                SELECT CASE WHEN a.WorkspaceId IS NULL OR typeof(a.WorkspaceId)='text' AND length(CAST(a.WorkspaceId AS BLOB))=36 THEN a.WorkspaceId END,
                    a.WorkspaceId IS NULL OR typeof(a.WorkspaceId)='text' AND length(CAST(a.WorkspaceId AS BLOB))=36
                FROM guild_agent_card_revisions r JOIN guild_agent_cards a ON a.CardId=r.CardId
                WHERE r.CardId=$source AND r.ContentId=$id AND r.Revision=$revision AND a.Available=1
                    AND a.SessionId IS $session AND a.CampaignId IS $campaign
                    AND (a.WorkspaceId IS NULL OR EXISTS(SELECT 1 FROM Campaigns w WHERE w.Id=a.WorkspaceId))
                    AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE d.OwnerKindCode=1 AND d.OwnerId=a.WorkspaceId)
                """);

            Add(card, "$source", Format(sourceId));
            Add(card, "$id", Format(contentId));
            Add(card, "$revision", revision);
            Add(card, "$session", session is { } sessionId ? Format(sessionId) : null);
            Add(card, "$campaign", campaign is { } campaignId ? Format(campaignId) : null);

            await using SqliteDataReader reader = await card.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return ContentMissing;

            if (reader.GetInt64(1) != 1 || !TryContentOwner(reader, 0, out _)) return Unavailable();

            await reader.DisposeAsync().ConfigureAwait(false);

            Result allowed = await policy.AuthorizeLocateAsync(caller, connection, transaction, null, session, campaign, token).ConfigureAwait(false);

            if (allowed.IsFailure) return ContentMissing;
        }
        else
        {
            string sql = reference.Kind switch
            {
                GuildContentKind.QuestGoal => "SELECT Id FROM guild_quests WHERE Id=$source AND GoalContentId=$id",
                GuildContentKind.QuestResult => "SELECT QuestId FROM guild_result_receipts WHERE ResultId=$source AND ContentId=$id",
                GuildContentKind.CircleBody => "SELECT QuestId FROM guild_envelopes WHERE Id=$source AND ContentId=$id UNION ALL SELECT QuestId FROM guild_continuations WHERE ContinuationId=$source AND ContentId=$id",
                GuildContentKind.CohortContribution => "SELECT PrimaryVotingQuestId FROM guild_cohort_contributions WHERE ResultId=$source AND ContentId=$id",
                GuildContentKind.Offer => "SELECT IssuerQuestId FROM guild_work_offers WHERE Id=$source AND GoalContentId=$id",
                GuildContentKind.Bid => "SELECT AuthorQuestId FROM guild_work_bids WHERE Id=$source AND NoteContentId=$id",
                GuildContentKind.CohortLedgerEntry => "SELECT coalesce(l.AuthorQuestId,c.IssuerQuestId) FROM guild_cohort_ledger l JOIN guild_cohorts c ON c.Id=l.CohortId WHERE l.Id=$source AND l.ContentId=$id AND c.Retired=0",
                _ => "SELECT NULL WHERE $source=$id AND 0",
            };

            await using SqliteCommand source = Command(connection, transaction, $"""
                WITH owning(QuestId) AS ({sql})
                SELECT CASE WHEN typeof(QuestId)='text' AND length(CAST(QuestId AS BLOB))=36 THEN QuestId END
                FROM owning LIMIT 2
                """);

            Add(source, "$source", Format(sourceId));

            Add(source, "$id", Format(contentId));

            await using SqliteDataReader sourceReader = await source.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await sourceReader.ReadAsync(token).ConfigureAwait(false)) return ContentMissing;

            if (sourceReader.IsDBNull(0) || !Guid.TryParseExact(sourceReader.GetString(0), "D", out Guid questId)
                || questId == Guid.Empty || await sourceReader.ReadAsync(token).ConfigureAwait(false)) return Unavailable();

            await sourceReader.DisposeAsync().ConfigureAwait(false);

            Result<GuildQuestStored> quest = await GuildQuestStore.ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

            if (quest.IsFailure) return quest.Error;

            if (quest.Value.Quest.SessionId != session || quest.Value.Quest.CampaignId != campaign) return Unavailable();

            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, quest.Value, token).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error.Code is ErrorCodes.Guild.Forbidden or ErrorCodes.Guild.OwnerUnavailable
                ? ContentMissing : visible.Error;
        }

        return reference;
    }

    private static bool TryContentOwner(SqliteDataReader reader, int ordinal, out Guid? owner)
    {
        owner = null;

        if (reader.IsDBNull(ordinal)) return true;

        if (!Guid.TryParseExact(reader.GetString(ordinal), "D", out Guid parsed) || parsed == Guid.Empty) return false;

        owner = parsed;

        return true;
    }

    private static Error ContentMissing => new(ErrorCodes.Guild.NotFound, "The Guild content revision was not found.");
}
