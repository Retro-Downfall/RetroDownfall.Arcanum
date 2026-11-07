using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Immutable attribution only; neither this record nor its labels convey execution authority.</summary>
internal sealed record GuildContinuationStored(Guid QuestId, Guid ContinuationId, GuildAuthorityKind Authority,
    GuildProducerIdentity? Producer, GuildDataRef ContentRef, long AcceptedRevision, DateTimeOffset CreatedAt);

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildContinuationStored>> GetContinuationAsync(GuildCallerContext caller,
        Guid questId, Guid continuationId, CancellationToken ct)
    {
        if (caller is null || questId == Guid.Empty || continuationId == Guid.Empty)
            return InvalidReceipt;

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        return await authority.InQuestTransactionAsync<GuildContinuationStored>(located.Value.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                if (caller.Execution is { } execution)
                {
                    Result<GuildGrant> live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                    if (live.IsFailure) return live.Error;
                }

                return await ReadContinuationWithinAsync(connection, transaction, found.Value, continuationId, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
    }

    private static async Task<Result<GuildContinuationStored>> ReadContinuationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored quest, Guid continuationId, CancellationToken ct)
    {
        // Only bounded immutable metadata crosses the storage boundary. Retention can erase the body
        // without erasing its attribution; resolving the body still needs the content disclosure lane.
        await using SqliteCommand command = Command(connection, transaction, """
            WITH content AS (
                SELECT ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body) AS ByteLength,
                    SessionId,CampaignId,0 AS Erased FROM guild_content
                WHERE ContentId=(SELECT ContentId FROM guild_continuations WHERE QuestId=$quest AND ContinuationId=$continuation)
                UNION ALL
                SELECT ContentId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,
                    SessionId,CampaignId,1 AS Erased FROM guild_content_erasure_receipts
                WHERE ContentId=(SELECT ContentId FROM guild_continuations WHERE QuestId=$quest AND ContinuationId=$continuation)
            )
            SELECT a.AuthorityCode,a.ProducerAgentId,a.ProducerRunId,a.ProducerQuestId,a.OperatorPrincipalId,
                a.ContentId,a.AcceptedRevision,a.CreatedAtUtc,c.Digest,c.MimeType,c.ByteLength,c.Erased
            FROM guild_continuations a JOIN content c ON c.ContentId=a.ContentId
            WHERE a.QuestId=$quest AND a.ContinuationId=$continuation
                AND typeof(a.AuthorityCode)='integer' AND a.AuthorityCode IN(0,1,2)
                AND (a.AuthorityCode<>2 OR EXISTS(SELECT 1 FROM guild_quests parent
                    WHERE parent.Id=$parent AND parent.Id=a.ProducerQuestId
                        AND parent.AgentId=a.ProducerAgentId AND parent.RunId=a.ProducerRunId
                        AND parent.RootQuestId=$root AND parent.InstallationGeneration=$generation))
                AND (a.ProducerAgentId IS NULL OR (typeof(a.ProducerAgentId)='text' AND length(CAST(a.ProducerAgentId AS BLOB))=36))
                AND (a.ProducerRunId IS NULL OR (typeof(a.ProducerRunId)='text' AND length(CAST(a.ProducerRunId AS BLOB))=36))
                AND (a.ProducerQuestId IS NULL OR (typeof(a.ProducerQuestId)='text' AND length(CAST(a.ProducerQuestId AS BLOB))=36))
                AND (a.OperatorPrincipalId IS NULL OR (typeof(a.OperatorPrincipalId)='text' AND length(CAST(a.OperatorPrincipalId AS BLOB))=36))
                AND typeof(a.ContentId)='text' AND length(CAST(a.ContentId AS BLOB))=36
                AND typeof(a.AcceptedRevision)='integer' AND a.AcceptedRevision BETWEEN 1 AND $revision
                AND typeof(a.CreatedAtUtc)='text' AND length(CAST(a.CreatedAtUtc AS BLOB))=28
                AND c.ContentKindCode=3 AND c.SourceRecordId=$continuation AND c.Revision=1
                AND c.SessionId IS $session AND c.CampaignId IS $campaign
                AND typeof(c.Digest)='blob' AND length(c.Digest)=32
                AND c.MimeType IN('text/plain','application/json')
                AND typeof(c.ByteLength)='integer' AND c.ByteLength BETWEEN 0 AND 65536
            """);

        Add(command, "$quest", Id(quest.Quest.Id));

        Add(command, "$continuation", Id(continuationId));

        Add(command, "$revision", quest.Quest.Revision);

        Add(command, "$parent", Id(quest.Quest.ParentQuestId));

        Add(command, "$root", Id(quest.Quest.RootQuestId));

        Add(command, "$generation", Id(quest.Issuance.InstallationGeneration));

        Add(command, "$session", Id(quest.Quest.SessionId));

        Add(command, "$campaign", Id(quest.Quest.CampaignId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

        try
        {
            GuildAuthorityKind authorityKind = (GuildAuthorityKind)reader.GetInt32(0);

            Guid? agent = ContinuationIdentity(reader, 1);

            Guid? run = ContinuationIdentity(reader, 2);

            Guid? producerQuest = ContinuationIdentity(reader, 3);

            Guid? principal = ContinuationIdentity(reader, 4);

            GuildProducerIdentity? producer;

            if (authorityKind == GuildAuthorityKind.LegacyUnknown && agent is null && run is null && producerQuest is null && principal is null)
                producer = null;
            else if (authorityKind == GuildAuthorityKind.Operator && agent is null && run is null && producerQuest is null && principal is not null)
                producer = new(GuildProducerKind.Operator, null, null, null, Id(principal), null, null, null);
            else if (authorityKind == GuildAuthorityKind.Parent && agent is not null && run is not null
                && producerQuest == quest.Quest.ParentQuestId && producerQuest is not null && principal is null)
                producer = new(GuildProducerKind.Run, agent, run, producerQuest, null, null, null, null);
            else
                return Unavailable;

            GuildDataRef content = new(ContinuationIdentity(reader, 5)!.Value, GuildContentKind.CircleBody, 1,
                Convert.ToHexString((byte[])reader.GetValue(8)), reader.GetString(9), reader.GetInt64(10), reader.GetInt32(11) == 1);

            GuildContinuationStored stored = new(quest.Quest.Id, continuationId, authorityKind, producer, content,
                reader.GetInt64(6), UtcInstantText.Parse(reader.GetString(7)));

            return await reader.ReadAsync(ct).ConfigureAwait(false) ? Unavailable : stored;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidCastException or OverflowException)
        {
            return Unavailable;
        }
    }

    private static Guid? ContinuationIdentity(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;

        string text = reader.GetString(ordinal);

        if (!Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty || Id(id) != text)
            throw new FormatException("The continuation identity is invalid.");

        return id;
    }
}
