using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>One exact content revision's erasure closure, shared by live and staged protected purges.</summary>
internal static class GuildContentLifecycle
{
    internal static string Predicate(string table, string parameter)
    {
        string content = $"SELECT CardId, Revision FROM guild_agent_card_revisions WHERE {CovenantIdentitySql.Keyed("ContentId", parameter)}";

        return table switch
        {
            "guild_agent_card_skills" => $"(CardId, Revision) IN ({content})",
            "guild_agent_card_revisions" => CovenantIdentitySql.Keyed("ContentId", parameter),
            "guild_content" => CovenantIdentitySql.Keyed("ContentId", parameter),
            _ => throw new ArgumentOutOfRangeException(nameof(table)),
        };
    }

    internal static async Task<Result> ValidateArtifactAsync(SqliteConnection connection, SqliteTransaction transaction,
        ArtifactSensitivityLabel label, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT SessionId, CampaignId, Revision, Digest, SensitivityCode, ProvenanceModeCode,
                   ExactGenerationIds, GenerationBloom, length(Body), Body
            FROM guild_content WHERE ContentId=$id
            """;

        _ = command.Parameters.AddWithValue("$id", Format(label.ArtifactId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || !OwnerMatches(reader, 0, label.SessionId) || !OwnerMatches(reader, 1, label.CampaignId)
            || reader.GetInt64(2) < 1 || (ulong)reader.GetInt64(2) != label.ArtifactRevision
            || reader.GetInt32(4) != (int)label.Sensitivity
            || reader.GetInt32(5) != (int)label.Provenance.Mode
            || reader.GetInt64(8) is < 0 or > GuildStore.MaximumContentBytes
            || reader.GetValue(3) is not byte[] digest || !digest.AsSpan().SequenceEqual(label.ArtifactContentDigest.Bytes))
        {
            return Invalid();
        }

        byte[]? exact = label.Provenance.Mode == GenerationProvenanceMode.Exact ? label.Provenance.ToCanonicalExactBytes() : null;

        byte[]? bloom = label.Provenance.Mode == GenerationProvenanceMode.BloomOverflow ? label.Provenance.ToCanonicalBloomBytes() : null;

        if (!BytesMatch(reader, 6, exact) || !BytesMatch(reader, 7, bloom)
            || reader.GetValue(9) is not byte[] body || !SHA256.HashData(body).AsSpan().SequenceEqual(digest))
        {
            return Invalid();
        }

        return Result.Success();
    }

    /// <summary>Runs inside the purger's transaction before any projection or body delete.</summary>
    internal static async Task PreparePurgeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string artifactKey, CancellationToken cancellationToken)
    {
        Guid artifactId = Guid.ParseExact(artifactKey, "N");

        Result<ArtifactSensitivityLabel?> labelled = await ArtifactSensitivityLedger.ReadLabelWithinAsync(
            connection, transaction, SensitiveArtifactKind.GuildContent, artifactId, cancellationToken).ConfigureAwait(false);

        if (labelled.IsFailure || labelled.Value is null
            || (await ValidateArtifactAsync(connection, transaction, labelled.Value, cancellationToken).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException("Guild protected content no longer matches its immutable sensitivity evidence.");
        }

        await RecordErasureAsync(connection, transaction, artifactId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task PrepareOrdinaryPurgeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string artifactKey, CancellationToken cancellationToken)
    {
        Guid artifactId = Guid.ParseExact(artifactKey, "N");

        await using SqliteCommand kind = connection.CreateCommand();

        kind.Transaction = transaction;

        kind.CommandText = "SELECT ContentKindCode FROM guild_content WHERE ContentId=$id AND SensitivityCode=0";

        kind.Parameters.AddWithValue("$id", Format(artifactId));

        object? stored = await kind.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // Card-cache pruning retains its existing lifecycle. Quest and later domain bodies
        // leave exact content-free evidence before their bytes are removed.
        if (stored is long contentKind && contentKind != 0)
        {
            await RecordErasureAsync(connection, transaction, artifactId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RecordErasureAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid artifactId, CancellationToken cancellationToken)
    {
        DateTimeOffset erasedAt = DateTimeOffset.UtcNow;

        await using SqliteCommand receipt = connection.CreateCommand();

        receipt.Transaction = transaction;

        receipt.CommandText = """
            INSERT INTO guild_content_erasure_receipts
                (ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,ByteLength,ErasedAtUtc)
            SELECT ContentId,SessionId,CampaignId,ContentKindCode,SourceRecordId,Revision,Digest,MimeType,length(Body),$now
            FROM guild_content WHERE ContentId=$id
            ON CONFLICT(ContentId) DO NOTHING;
            """;

        _ = receipt.Parameters.AddWithValue("$id", Format(artifactId));

        _ = UtcInstantSql.AddParameter(receipt, "$now", erasedAt);

        _ = await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand verification = connection.CreateCommand();

        verification.Transaction = transaction;

        verification.CommandText = """
            SELECT COUNT(*) FROM guild_content c JOIN guild_content_erasure_receipts r ON r.ContentId=c.ContentId
            WHERE c.ContentId=$id AND r.SessionId IS c.SessionId AND r.CampaignId IS c.CampaignId
              AND r.ContentKindCode=c.ContentKindCode AND r.SourceRecordId=c.SourceRecordId
              AND r.Revision=c.Revision AND r.Digest=c.Digest AND r.MimeType=c.MimeType AND r.ByteLength=length(c.Body)
            """;

        _ = verification.Parameters.AddWithValue("$id", Format(artifactId));

        if (!Equals(1L, await verification.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
        {
            throw new InvalidDataException("Guild content erasure conflicts with retained revision evidence.");
        }

        await using SqliteCommand head = connection.CreateCommand();

        head.Transaction = transaction;

        head.CommandText = """
            UPDATE guild_agent_cards SET ContentId=NULL,Available=0,ReasonCode=$reason,UpdatedAtUtc=$now
            WHERE ContentId=$id
            """;

        _ = head.Parameters.AddWithValue("$id", Format(artifactId));

        _ = UtcInstantSql.AddParameter(head, "$now", erasedAt);

        _ = head.Parameters.AddWithValue("$reason", ErrorCodes.Guild.ContentErased);

        _ = await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand quest = connection.CreateCommand();

        quest.Transaction = transaction;

        quest.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='guild_quests'";

        if (Equals(1L, await quest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
        {
            quest.CommandText = """
                UPDATE guild_quests SET Name=NULL,Revision=Revision+1,UpdatedAtUtc=$now
                WHERE GoalContentId=$id AND Name IS NOT NULL;
                UPDATE guild_result_outbox SET DeliveryStateCode=3,Revision=Revision+1,AcknowledgedAtUtc=$now
                WHERE DeliveryStateCode=1 AND QuestId IN(SELECT QuestId FROM guild_result_receipts WHERE ContentId=$id);
                """;

            quest.Parameters.AddWithValue("$id", Format(artifactId));

            UtcInstantSql.AddParameter(quest, "$now", erasedAt);

            _ = await quest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool OwnerMatches(SqliteDataReader reader, int ordinal, Guid? owner) => owner is { } id
        ? !reader.IsDBNull(ordinal) && Guid.TryParse(reader.GetString(ordinal), out Guid stored) && stored == id
        : reader.IsDBNull(ordinal);

    private static bool BytesMatch(SqliteDataReader reader, int ordinal, byte[]? expected) => expected is null
        ? reader.IsDBNull(ordinal)
        : reader.GetValue(ordinal) is byte[] actual && actual.AsSpan().SequenceEqual(expected);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Error Invalid() => new(ErrorCodes.Covenant.IntegrityFailure,
        "Guild protected content no longer matches its immutable sensitivity evidence.");
}
