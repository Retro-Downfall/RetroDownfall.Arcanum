using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal enum GuildResultSourceState { Clean, Erased, ProtectedUnavailable }

internal sealed partial class GuildCohortStore
{
    /// <summary>Current source integrity/visibility proof only; callers authorize their Cohort and reader separately.</summary>
    internal static async Task<Result<GuildResultSourceState>> ReadCurrentResultSourceStateWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid primaryQuestId, Guid resultId, CancellationToken ct)
    {
        var source = await GuildQuestStore.ReadWithinAsync(connection, transaction, primaryQuestId, ct).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        GuildQuestDto quest = source.Value.Quest;

        if (source.Value.Retired || quest.State != QuestState.Completed || quest.ResultReceiptId != resultId
            || quest.ResultRef is not { Kind: GuildContentKind.QuestResult } reference) return Unavailable;

        Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, quest.SessionId, quest.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure) return owner.Error;

        byte[] digest = Convert.FromHexString(reference.Digest);

        await using (SqliteCommand receipt = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_result_receipts WHERE QuestId=$quest AND ResultId=$result AND OutcomeCode=3
                AND ContentId=$content AND ContentRevision=$revision AND Digest=$digest AND MimeType=$mime AND ByteLength=$bytes)
            """))
        {
            Add(receipt, "$quest", Id(primaryQuestId));
            Add(receipt, "$result", Id(resultId));
            Add(receipt, "$content", Id(reference.ContentId));
            Add(receipt, "$revision", reference.Revision);
            Add(receipt, "$digest", digest);
            Add(receipt, "$mime", reference.MimeType);
            Add(receipt, "$bytes", reference.ByteLength);

            if (!Equals(1L, await receipt.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Unavailable;
        }

        var content = await ReadCurrentContentWithinAsync(connection, transaction, resultId, quest.SessionId, quest.CampaignId,
            reference.ContentId, reference.Kind, reference.Revision, reference.MimeType, reference.ByteLength,
            digest, null, null, ct, true).ConfigureAwait(false);

        if (content.IsFailure) return content.Error;

        if (content.Value is not null) return GuildResultSourceState.Clean;

        await using SqliteCommand erased = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_content_erasure_receipts WHERE ContentId=$content AND SourceRecordId=$result
                AND ContentKindCode=$kind AND Revision=$revision AND Digest=$digest AND MimeType=$mime AND ByteLength=$bytes
                AND SessionId IS $session AND CampaignId IS $campaign)
            """);

        Add(erased, "$content", Id(reference.ContentId));
        Add(erased, "$result", Id(resultId));
        Add(erased, "$kind", (int)GuildContentKind.QuestResult);
        Add(erased, "$revision", reference.Revision);
        Add(erased, "$digest", digest);
        Add(erased, "$mime", reference.MimeType);
        Add(erased, "$bytes", reference.ByteLength);
        Add(erased, "$session", quest.SessionId?.ToString("D").ToUpperInvariant());
        Add(erased, "$campaign", quest.CampaignId?.ToString("D").ToUpperInvariant());

        return Equals(1L, await erased.ExecuteScalarAsync(ct).ConfigureAwait(false))
            ? GuildResultSourceState.Erased : GuildResultSourceState.ProtectedUnavailable;
    }

    internal static async Task<Result<bool>> IsCurrentResultSourceCleanWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid primaryQuestId, Guid resultId, CancellationToken ct)
    {
        var state = await ReadCurrentResultSourceStateWithinAsync(connection, transaction, primaryQuestId, resultId, ct).ConfigureAwait(false);

        return state.IsFailure ? state.Error : state.Value == GuildResultSourceState.Clean;
    }

    private static Task<Result<GuildDataRef?>> ReadSelectedContentWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SelectedEntry entry, Guid contentId, GuildContentKind kind, long revision, string mime, long bytes,
        byte[]? expectedDigest, bool agreementProof, CancellationToken ct, bool allowBytes = true) =>
        ReadCurrentContentWithinAsync(connection, transaction, entry.Result, entry.Session, entry.Campaign, contentId, kind,
            revision, mime, bytes, expectedDigest, agreementProof ? entry.Proof : null,
            agreementProof ? entry.Agreement : null, ct, allowBytes);

    private static async Task<Result<GuildDataRef?>> ReadCurrentContentWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid resultId, Guid? session, Guid? campaign, Guid contentId, GuildContentKind kind, long revision, string mime, long bytes,
        byte[]? expectedDigest, byte[]? selectedProof, byte[]? selectedAgreement, CancellationToken ct, bool allowBytes)
    {
        byte[] digest;

        bool erased;

        bool protectedContent;

        await using (SqliteCommand metadata = Command(connection, transaction, """
            SELECT SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,Bytes,Erased,Protected,Valid
            FROM (
                SELECT CASE WHEN SessionId IS NULL OR length(CAST(SessionId AS BLOB))=36 THEN SessionId END AS SessionId,
                    CASE WHEN CampaignId IS NULL OR length(CAST(CampaignId AS BLOB))=36 THEN CampaignId END AS CampaignId,
                    ContentKindCode,CASE WHEN length(CAST(SourceRecordId AS BLOB))=36 THEN SourceRecordId END AS SourceRecordId,
                    Revision,CASE WHEN typeof(Digest)='blob' AND length(Digest)=32 THEN Digest END AS Digest,
                    CASE WHEN MimeType IN('text/plain','application/json') THEN MimeType END AS MimeType,
                    length(Body) AS Bytes,0 AS Erased,
                    SensitivityCode<>0 OR ProvenanceModeCode<>0 OR EXISTS(SELECT 1 FROM artifact_sensitivity
                        WHERE ArtifactKindCode=17 AND lower(replace(ArtifactId,'-',''))=lower(replace($id,'-',''))) AS Protected,
                    (SessionId IS NULL OR typeof(SessionId)='text' AND length(CAST(SessionId AS BLOB))=36)
                        AND (CampaignId IS NULL OR typeof(CampaignId)='text' AND length(CAST(CampaignId AS BLOB))=36)
                        AND typeof(ContentKindCode)='integer' AND typeof(Revision)='integer' AND Revision>=1
                        AND typeof(Body)='blob' AND length(Body)<=65536
                        AND typeof(SensitivityCode)='integer' AND SensitivityCode IN(0,1)
                        AND typeof(ProvenanceModeCode)='integer' AND ProvenanceModeCode IN(0,1,2) AS Valid
                FROM guild_content WHERE ContentId=$id
                UNION ALL
                SELECT CASE WHEN SessionId IS NULL OR length(CAST(SessionId AS BLOB))=36 THEN SessionId END,
                    CASE WHEN CampaignId IS NULL OR length(CAST(CampaignId AS BLOB))=36 THEN CampaignId END,
                    ContentKindCode,CASE WHEN length(CAST(SourceRecordId AS BLOB))=36 THEN SourceRecordId END,
                    Revision,CASE WHEN typeof(Digest)='blob' AND length(Digest)=32 THEN Digest END,
                    CASE WHEN MimeType IN('text/plain','application/json') THEN MimeType END,
                    ByteLength,1,0,
                    (SessionId IS NULL OR typeof(SessionId)='text' AND length(CAST(SessionId AS BLOB))=36)
                        AND (CampaignId IS NULL OR typeof(CampaignId)='text' AND length(CAST(CampaignId AS BLOB))=36)
                        AND typeof(ContentKindCode)='integer' AND typeof(Revision)='integer' AND Revision>=1
                        AND typeof(ByteLength)='integer' AND ByteLength BETWEEN 0 AND 65536
                FROM guild_content_erasure_receipts WHERE ContentId=$id
            )
            """))
        {
            Add(metadata, "$id", Id(contentId));

            await using SqliteDataReader reader = await metadata.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(10) != 1
                || reader.IsDBNull(3) || reader.IsDBNull(5) || reader.IsDBNull(6)
                || (reader.IsDBNull(0) ? null : reader.GetString(0)) != session?.ToString("D").ToUpperInvariant()
                || (reader.IsDBNull(1) ? null : reader.GetString(1)) != campaign?.ToString("D").ToUpperInvariant()
                || reader.GetInt32(2) != (int)kind || reader.GetString(3) != Id(resultId)
                || reader.GetInt64(4) != revision || reader.GetString(6) != mime || reader.GetInt64(7) != bytes)
                return Unavailable;

            erased = reader.GetInt64(8) == 1;

            protectedContent = reader.GetInt64(9) != 0;

            if (protectedContent && !erased)
            {
                if (await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;

                return Result<GuildDataRef?>.Success(null);
            }

            digest = (byte[])reader.GetValue(5);

            if (expectedDigest is not null && !digest.AsSpan().SequenceEqual(expectedDigest)) return Unavailable;

            if (await reader.ReadAsync(ct).ConfigureAwait(false)) return Unavailable;
        }

        // No protected bytes, digest or new reference capability escapes without a qualified
        // disclosure adapter. Current labels are checked independently of staged selection data.
        if (erased || protectedContent || !allowBytes) return Result<GuildDataRef?>.Success(null);

        await using SqliteCommand body = Command(connection, transaction, """
            SELECT CASE WHEN typeof(Body)='blob' AND length(Body)=$bytes AND length(Body)<=65536 THEN Body END
            FROM guild_content WHERE ContentId=$id AND Revision=$revision AND Digest=$digest
                AND SensitivityCode=0 AND ProvenanceModeCode=0
                AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity WHERE ArtifactKindCode=17
                    AND lower(replace(ArtifactId,'-',''))=lower(replace($id,'-','')))
            """);

        Add(body, "$id", Id(contentId));
        Add(body, "$revision", revision);
        Add(body, "$digest", digest);
        Add(body, "$bytes", bytes);

        if (await body.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] value
            || !SHA256.HashData(value).AsSpan().SequenceEqual(digest)) return Unavailable;

        if (selectedProof is not null && selectedAgreement is not null)
        {
            var accepted = GuildCohortResultPolicy.ValidateResult(new(mime), mime, value);

            if (accepted.IsFailure || !Convert.FromHexString(accepted.Value).AsSpan().SequenceEqual(selectedAgreement)
                || !MatchesSelectedProof(selectedProof, value, mime)) return Unavailable;
        }

        return Result<GuildDataRef?>.Success(new(contentId, kind, revision, Convert.ToHexString(digest), mime, bytes, false));
    }

    private static bool MatchesSelectedProof(byte[] expected, byte[] value, string mime)
    {
        bool Matches(string originalMime)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData("guild-primary-proof-v1\n"u8);

            Span<byte> length = stackalloc byte[8];

            BinaryPrimitives.WriteInt64BigEndian(length, 1);

            hash.AppendData(length);

            byte[] encodedMime = Encoding.UTF8.GetBytes(originalMime);

            BinaryPrimitives.WriteInt64BigEndian(length, encodedMime.Length);

            hash.AppendData(length);

            hash.AppendData(encodedMime);

            BinaryPrimitives.WriteInt64BigEndian(length, value.Length);

            hash.AppendData(length);

            hash.AppendData(value);

            return hash.GetHashAndReset().AsSpan().SequenceEqual(expected);
        }

        return Matches(mime) || mime == "application/json" && Matches("text/plain");
    }
}
