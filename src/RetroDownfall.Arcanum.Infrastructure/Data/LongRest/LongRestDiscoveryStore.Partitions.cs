using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    // These projections inspect structural label metadata in SQLite. Neither provenance bytes nor
    // source text are materialized, and an opaque digest never grants access to either.
    private static readonly string ProtectedPartitionSources = $"""
        WITH eligible AS (
            SELECT memory.Id, memory.SessionId, memory.CampaignId, version.SensitivityCode
            FROM long_rest_sources source JOIN saga_memories memory ON memory.Id = source.MemoryId
            LEFT JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
            LEFT JOIN annal_heads head ON head.ClaimId = claim.ClaimId
            LEFT JOIN annal_versions version ON version.VersionId = head.CurrentVersionId
            WHERE memory.ScopeKindCode = $scope AND memory.CampaignId IS $campaign AND ({SourceOwnerEligible})
              AND {SourceLifecycleEligible}
        ), labeled AS (
            SELECT memory.Id, label.SensitivityDigest,
                count(label.LabelId) OVER (PARTITION BY memory.Id) AS LabelCount,
                CASE WHEN label.SensitivityCode = 1 AND length(label.SensitivityDigest) = 32
                    AND length(label.ArtifactContentDigest) = 32 AND length(label.ArtifactLabelDigest) = 32
                    AND label.ArtifactRevision >= 0 AND label.SessionId IS memory.SessionId COLLATE NOCASE
                    AND label.CampaignId IS memory.CampaignId COLLATE NOCASE
                    AND label.ArtifactId = upper(memory.Id) AND length(label.ArtifactId) = 36
                    AND memory.Id = lower(memory.Id) AND length(memory.Id) = 36
                    AND substr(memory.Id, 9, 1) = '-' AND substr(memory.Id, 14, 1) = '-'
                    AND substr(memory.Id, 19, 1) = '-' AND substr(memory.Id, 24, 1) = '-'
                    AND length(replace(memory.Id, '-', '')) = 32 AND replace(memory.Id, '-', '') NOT GLOB '*[^0-9a-f]*'
                    AND replace(memory.Id, '-', '') <> '00000000000000000000000000000000'
                    AND ((label.ProvenanceModeCode = 1 AND label.GenerationBloom IS NULL
                          AND length(label.ExactGenerationIds) BETWEEN 16 AND 128 AND length(label.ExactGenerationIds) % 16 = 0
                          AND NOT EXISTS (SELECT 1 FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7))
                            WHERE column1 * 16 < length(label.ExactGenerationIds)
                              AND (substr(label.ExactGenerationIds, column1 * 16 + 1, 16) = zeroblob(16)
                                OR (column1 > 0 AND substr(label.ExactGenerationIds, (column1 - 1) * 16 + 1, 16)
                                  >= substr(label.ExactGenerationIds, column1 * 16 + 1, 16)))))
                        OR (label.ProvenanceModeCode = 2 AND label.ExactGenerationIds IS NULL
                          AND length(label.GenerationBloom) = 32 AND label.GenerationBloom <> zeroblob(32)))
                    THEN 1 ELSE 0 END AS Valid
            FROM eligible memory LEFT JOIN artifact_sensitivity label ON label.ArtifactKindCode = 6
                AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', ''))
            WHERE label.LabelId IS NOT NULL OR COALESCE(memory.SensitivityCode, 0) <> 0
        )
        """;

    public async Task<Result<CovenantDigest?>> ReadNextProtectedPartitionAsync(CanonicalCampaignContext scope,
        CovenantDigest? after, CancellationToken ct)
    {
        try
        {
            if (after is { IsValid: false })
            {
                throw new InvalidDataException();
            }

            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            await using SqliteCommand command = Command(connection, transaction,
                ProtectedPartitionSources + " SELECT count(*) FROM labeled WHERE LabelCount <> 1 OR Valid <> 1;",
                ("$scope", scope.IsCampaignBound ? 2 : 1), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null));

            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0)
            {
                throw new InvalidDataException();
            }

            command.CommandText = ProtectedPartitionSources + " SELECT SensitivityDigest FROM labeled WHERE $after IS NULL OR SensitivityDigest > $after ORDER BY SensitivityDigest LIMIT 1;";

            command.Parameters.AddWithValue("$after", (object?)after?.Bytes ?? DBNull.Value);

            object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            return Result<CovenantDigest?>.Success(value is byte[] digest ? new CovenantDigest(digest) : null);
        }
        catch (Exception exception) when (StorageFailure(exception) || exception is InvalidOperationException)
        {
            return new Error(ErrorCodes.LongRest.StaleInput, "The protected partition metadata is unavailable or ambiguous.");
        }
    }
}
