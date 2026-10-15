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

    // One pass over the protected partition projection answers both questions the turn asks of it: how many sources are ambiguous or
    // mis-labelled (any makes the whole probe refuse) and which partition digest follows the cursor. The two used to be separate
    // statements, each rebuilding the projection, and the probe runs once per protected partition on every eligible Session turn.
    internal static readonly string ProtectedPartitionProbeSql = ProtectedPartitionSources + "\n" + """
        SELECT count(*) FILTER (WHERE LabelCount <> 1 OR Valid <> 1),
            min(SensitivityDigest) FILTER (WHERE $after IS NULL OR SensitivityDigest > $after)
        FROM labeled;
        """;

    // The early-out for an installation with nothing protected in it: no kind-6 label exists and no stored version carries a sensitivity,
    // so the projection above is empty whatever the scope, and the answer is "no partition" without building it. It is deliberately a
    // superset test (any label, any version), so it can only decline to short-circuit, never admit a source the projection would refuse.
    internal const string ProtectedPartitionAbsentSql = """
        SELECT NOT EXISTS (SELECT 1 FROM artifact_sensitivity WHERE ArtifactKindCode = 6)
            AND NOT EXISTS (SELECT 1 FROM annal_versions WHERE SensitivityCode <> 0);
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

            LongRestVerificationCounters.Transaction(deferred: true);

            if (after is null)
            {
                await using SqliteCommand absent = Command(connection, transaction, ProtectedPartitionAbsentSql);

                if ((long)(await absent.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 1)
                {
                    return Result<CovenantDigest?>.Success(null);
                }
            }

            LongRestVerificationCounters.PartitionPass();

            await using SqliteCommand command = Command(connection, transaction, ProtectedPartitionProbeSql,
                ("$scope", scope.IsCampaignBound ? 2 : 1), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null),
                ("$after", after?.Bytes));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(0) != 0)
            {
                throw new InvalidDataException();
            }

            return Result<CovenantDigest?>.Success(reader.IsDBNull(1) ? null : new CovenantDigest((byte[])reader[1]));
        }
        catch (Exception exception) when (StorageFailure(exception) || exception is InvalidOperationException)
        {
            return new Error(ErrorCodes.LongRest.StaleInput, "The protected partition metadata is unavailable or ambiguous.");
        }
    }
}
