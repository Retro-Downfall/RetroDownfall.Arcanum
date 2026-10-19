using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemoryEvaluationSourceReader
{
    private async Task<MemoryEvaluationSourceCoordinate> ReadSagaAsync(SqliteConnection connection, SqliteTransaction transaction,
        MemoryEvaluationSagaTarget target, Guid? campaign, CovenantCanonicalHashWriter metadata, DateTimeOffset now, CancellationToken ct, bool intervention = true, int remainingBytes = MemoryEvaluationLimits.RenderedMemoryBytes)
    {
        bool suppression = await LongRestRecallSql.AvailableAsync(connection, ct, transaction).ConfigureAwait(false);

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT c.ClaimId, v.VersionId, v.Revision, v.ContentHashFormatCode, v.ContentHash, v.OriginCode,
                v.ScopeKindCode, v.CampaignId, v.ValidFromUtc, v.ValidToUtc, v.RecordedAtUtc, v.SensitivityCode,
                m.SessionId, v.SourceSessionId, m.ScopeKindCode, m.CampaignId, m.RetiredAtUtc,
                length(CAST(m.Content AS BLOB)), h.CurrentVersionId, v.OperationCode,
                (SELECT successor.RecordedAtUtc FROM annal_versions successor WHERE successor.PredecessorVersionId = v.VersionId),
                EXISTS(SELECT 1 FROM saga_memory_embeddings e WHERE e.MemoryId = m.Id),
                EXISTS(SELECT 1 FROM Sessions owner JOIN session_campaign_bindings binding ON binding.SessionId = owner.Id
                    WHERE owner.Id = m.SessionId COLLATE NOCASE AND binding.BindingKindCode = m.ScopeKindCode AND binding.CampaignId IS m.CampaignId),
                {LongRestRecallSql.Eligible("m", suppression)},
                EXISTS(SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                    AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(m.Id, '-', ''))),
                m.PinnedAtUtc, (SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = m.Id)
            FROM saga_memories m
            JOIN annal_claims c ON c.SubjectStoreCode = 1 AND c.SubjectId = m.Id
            JOIN annal_heads h ON h.ClaimId = c.ClaimId AND h.SubjectStoreCode = 1
            JOIN annal_versions v ON v.ClaimId = c.ClaimId
            WHERE m.Id = $id AND v.VersionId = $version
                AND ($historical = 1 OR v.Revision = h.CurrentRevision AND v.OperationCode = h.CurrentOperationCode)
            """, ("$id", target.MemoryId), ("$version", target.VersionId), ("$historical", intervention ? 0 : 1));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        Require(await reader.ReadAsync(ct).ConfigureAwait(false));

        int scope = reader.GetInt32(6);

        Guid? sourceCampaign = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7));

        DateTimeOffset from = UtcInstantText.Parse(reader.GetString(8));

        DateTimeOffset? until = reader.IsDBNull(9) ? null : UtcInstantText.Parse(reader.GetString(9));

        Require(target.HashFormat == AnnalContentHashFormat.LegacyStoreDigest && scope is 1 or 2 && (scope == 1 ? sourceCampaign is null : sourceCampaign == campaign)
            && scope == reader.GetInt32(14) && sourceCampaign == (reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)))
            && (!intervention || reader.IsDBNull(16) && reader.GetInt64(17) <= remainingBytes
                && reader.GetString(18) == target.VersionId && reader.IsDBNull(20) && reader.GetBoolean(21) && reader.GetBoolean(23))
            && reader.GetInt32(19) != 3 && (reader.IsDBNull(12) ? scope == 1 : reader.GetBoolean(22))
            && (!intervention || from <= now && UtcInstantText.Parse(reader.GetString(10)) <= now && (until is null || until > now))
            && reader.GetInt32(3) == (int)target.HashFormat && new CovenantDigest((byte[])reader[4]) == target.ContentDigest);

        HashRow(metadata, reader);

        MemoryEvaluationSourceCoordinate coordinate = new(intervention ? MemoryEvaluationCoordinateRole.Intervention : MemoryEvaluationCoordinateRole.Dependency, MemoryEvaluationSource.Saga,
            target.MemoryId, reader.GetString(0), target.VersionId, (int)target.HashFormat, target.ContentDigest, null,
            (AnnalOrigin)reader.GetInt32(5), (MemoryEvaluationScopeKind)scope, sourceCampaign, from, until,
            UtcInstantText.Parse(reader.GetString(10)), reader.IsDBNull(20) ? null : UtcInstantText.Parse(reader.GetString(20)),
            reader.IsDBNull(16) ? MemoryEvaluationSourceLifecycle.Active : MemoryEvaluationSourceLifecycle.Retired, (ContentSensitivity)reader.GetInt32(11),
            reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)).ToString("D").ToUpperInvariant(), reader.IsDBNull(13) ? null : reader.GetString(13), Revision: reader.GetInt32(2));

        Require(!await reader.ReadAsync(ct).ConfigureAwait(false));

        await reader.DisposeAsync().ConfigureAwait(false);

        await VerifyOwnersAsync(connection, transaction, coordinate, metadata, ct).ConfigureAwait(false);

        ArtifactSensitivityLabel? label = await ReadLabelAsync(connection, transaction, coordinate, metadata, ct).ConfigureAwait(false);

        return coordinate with { Sensitivity = label?.Sensitivity ?? coordinate.Sensitivity };
    }
}
