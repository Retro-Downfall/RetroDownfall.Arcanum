using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemorySourceProofReader
{
    private async Task<MemoryEvaluationSourceCoordinate> ReadLexiconAsync(SqliteConnection connection, SqliteTransaction transaction,
        MemoryEvaluationLexiconTarget target, Guid? campaign, CovenantCanonicalHashWriter metadata, DateTimeOffset now, CancellationToken ct, bool intervention = true)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT c.ClaimId, v.Revision, v.ContentHashFormatCode, v.ContentHash, v.OriginCode, v.ScopeKindCode, v.CampaignId,
                v.ValidFromUtc, v.ValidToUtc, v.RecordedAtUtc, v.SensitivityCode, v.SourceSessionId,
                e.ScopeCampaignId, e.RetiredAtUtc, e.PinnedAtUtc, e.CurationGeneration,
                length(CAST(e.FactsJson AS BLOB)) + length(CAST(e.FactsText AS BLOB)) + length(CAST(e.Name AS BLOB)) + length(CAST(e.Type AS BLOB)),
                h.CurrentVersionId, v.OperationCode,
                (SELECT successor.RecordedAtUtc FROM annal_versions successor WHERE successor.PredecessorVersionId = v.VersionId),
                EXISTS(SELECT 1 FROM lexicon_entries shadow WHERE shadow.NameNormalized = e.NameNormalized
                    AND shadow.ScopeCampaignId = $campaign AND shadow.RetiredAtUtc IS NULL),
                e.NameNormalized, e.UpdatedAt, h.CurrentRevision, h.CurrentOperationCode
            FROM lexicon_entries e
            JOIN annal_claims c ON c.SubjectStoreCode = 2 AND c.SubjectId = e.Id
            JOIN annal_heads h ON h.ClaimId = c.ClaimId AND h.SubjectStoreCode = 2
            JOIN annal_versions v ON v.ClaimId = c.ClaimId
            WHERE e.Id = $id AND v.VersionId = $version
            """, ("$id", target.EntryId.ToString("N")), ("$version", target.VersionId), ("$campaign", campaign?.ToString("D")));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Require(await reader.ReadAsync(ct).ConfigureAwait(false));

        int scope = reader.GetInt32(5);

        Guid? sourceCampaign = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6));

        DateTimeOffset from = UtcInstantText.Parse(reader.GetString(7));

        DateTimeOffset? until = reader.IsDBNull(8) ? null : UtcInstantText.Parse(reader.GetString(8));

        Require(scope is 1 or 2 && (scope == 1 ? sourceCampaign is null : sourceCampaign == campaign)
            && sourceCampaign == (reader.GetString(12).Length == 0 ? null : Guid.Parse(reader.GetString(12)))
            && reader.GetInt64(15) > 0 && reader.GetInt32(18) != 3
            && reader.GetInt32(2) == (int)target.HashFormat && new CovenantDigest((byte[])reader[3]) == target.ContentDigest
            && (!intervention || reader.IsDBNull(13) && reader.GetInt64(16) <= _limits.RenderedMemoryBytes
                && reader.GetString(17) == target.VersionId && reader.IsDBNull(19)
                && reader.GetInt32(1) == reader.GetInt32(23) && reader.GetInt32(18) == reader.GetInt32(24)
                && !(sourceCampaign is null && campaign is not null && reader.GetBoolean(20))
                && from <= now && UtcInstantText.Parse(reader.GetString(9)) <= now && (until is null || until > now)));

        HashRow(metadata, reader);

        MemoryEvaluationSourceCoordinate coordinate = new(intervention ? MemoryEvaluationCoordinateRole.Intervention : MemoryEvaluationCoordinateRole.Dependency,
            MemoryEvaluationSource.Lexicon, target.EntryId.ToString("N"), reader.GetString(0), target.VersionId, (int)target.HashFormat,
            target.ContentDigest, null, (AnnalOrigin)reader.GetInt32(4), (MemoryEvaluationScopeKind)scope, sourceCampaign,
            from, until, UtcInstantText.Parse(reader.GetString(9)), reader.IsDBNull(19) ? null : UtcInstantText.Parse(reader.GetString(19)),
            reader.IsDBNull(13) ? MemoryEvaluationSourceLifecycle.Active : MemoryEvaluationSourceLifecycle.Retired,
            (ContentSensitivity)reader.GetInt32(10), null, reader.IsDBNull(11) ? null : reader.GetString(11), Revision: reader.GetInt32(1));

        Require(!await reader.ReadAsync(ct).ConfigureAwait(false));

        await reader.DisposeAsync().ConfigureAwait(false);

        await VerifyOwnersAsync(connection, transaction, coordinate, metadata, ct).ConfigureAwait(false);

        ArtifactSensitivityLabel? label = await ReadLabelAsync(connection, transaction, coordinate, metadata, ct).ConfigureAwait(false);

        await HashLexiconProvenanceAsync(connection, transaction, coordinate, metadata, ct).ConfigureAwait(false);

        return coordinate with { Sensitivity = label?.Sensitivity ?? coordinate.Sensitivity };
    }

    private static async Task<long> ReadSelectedLexiconContentBytesAsync(SqliteConnection connection, SqliteTransaction transaction, string subjectId, CancellationToken ct) =>
        (long)(await ScalarAsync(connection, transaction,
            "SELECT length(CAST(FactsJson AS BLOB)) + length(CAST(FactsText AS BLOB)) + length(CAST(Name AS BLOB)) + length(CAST(Type AS BLOB)) FROM lexicon_entries WHERE Id = $id",
            ct, ("$id", subjectId)).ConfigureAwait(false))!;

    private static async Task<string> HydrateLexiconAsync(SqliteConnection connection, SqliteTransaction transaction,
        MemoryEvaluationSourceCoordinate source, ArtifactSensitivityLabel? label, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt FROM lexicon_entries WHERE Id = $id", ("$id", source.SubjectId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Require(await reader.ReadAsync(ct).ConfigureAwait(false));

        using JsonDocument facts = JsonDocument.Parse(reader.GetString(3));

        Require(facts.RootElement.ValueKind == JsonValueKind.Array);

        string[] values = facts.RootElement.EnumerateArray().Select(value => value.GetString()!).ToArray();

        var normalized = LexiconValueNormalizer.NormalizeCorrection(reader.GetString(0), reader.GetString(2), values);

        Require(normalized.IsSuccess);

        LexiconCanonicalValue canonical = normalized.Value;

        Require(canonical.Name == reader.GetString(0) && canonical.NameNormalized == reader.GetString(1)
            && canonical.Type == reader.GetString(2) && canonical.Facts.SequenceEqual(values) && canonical.FactsText == reader.GetString(4));

        byte[] digest = source.HashFormat == (int)AnnalContentHashFormat.LegacyStoreDigest
            ? AnnalContentDigest.ForLexiconEntry(canonical.Type, canonical.FactsText) : LexiconSnapshotDigest.Compute(canonical);

        Require(new CovenantDigest(digest) == source.ContentDigest
            && (label is null || label.ArtifactContentDigest == LexiconSnapshotDigest.ComputeDerivedArtifactContentDigest(canonical)));

        return SystemPromptBuilder.RenderEvaluationLexicon(new(Guid.Parse(source.SubjectId), canonical.Name, canonical.Type, [.. canonical.Facts],
            UtcInstantText.Parse(reader.GetString(5)), ScopeCampaignId: source.CampaignId));
    }

    private static async Task HashLexiconProvenanceAsync(SqliteConnection connection, SqliteTransaction transaction,
        MemoryEvaluationSourceCoordinate source, CovenantCanonicalHashWriter metadata, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT FactOrdinal, SessionId, AttachmentId, LogicalKey, AttachmentVersion, AttachmentContentHash, MaterializedAt, SourceType
            FROM lexicon_annal_fact_provenance WHERE AnnalVersionId = $version ORDER BY FactOrdinal LIMIT 129
            """, ("$version", source.VersionId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        int count = 0;

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Require(++count <= 128);

            HashRow(metadata, reader);
        }

        metadata.WriteInt32(count);
    }
}
