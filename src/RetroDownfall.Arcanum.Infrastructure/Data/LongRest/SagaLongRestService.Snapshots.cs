using System.Data.Common;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class SagaLongRestService
{
    private static async Task<Result<LoadedSnapshot>> ReadSnapshotAsync(
        DbConnection connection, DbTransaction transaction, LongRestTarget target, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // Metadata and labels are read before plaintext. A protected row never materializes Content
        // through this ordinary policy path, even when its historical Annals sensitivity was None.
        command.CommandText =
            """
            SELECT v.ClaimId, v.VersionId, v.Revision, v.ContentHashFormatCode,
                   COALESCE(v.ContentHash, (
                       SELECT prior.ContentHash FROM annal_versions prior
                       WHERE prior.VersionId = v.PredecessorVersionId AND prior.ClaimId = v.ClaimId
                         AND prior.OperationCode <> 3 AND prior.ContentHashFormatCode = v.ContentHashFormatCode)),
                   v.OriginCode, v.SourceSessionId, v.ScopeKindCode, v.CampaignId, v.SensitivityCode,
                   v.ValidFromUtc, v.ValidToUtc, v.RecordedAtUtc, m.RetiredAtUtc, m.PinnedAtUtc,
                   h.CurrentVersionId = v.VersionId,
                   EXISTS (SELECT 1 FROM saga_memory_embeddings e WHERE e.MemoryId = m.Id),
                   EXISTS (SELECT 1 FROM artifact_sensitivity a WHERE a.ArtifactKindCode = 6
                       AND lower(replace(a.ArtifactId, '-', '')) = lower(replace(m.Id, '-', ''))),
                   EXISTS (SELECT 1 FROM long_rest_suppressions s WHERE s.SourceVersionId = v.VersionId),
                   EXISTS (SELECT 1 FROM long_rest_receipts r WHERE r.OutcomeCode = 1 AND r.SurvivorVersionId = v.VersionId),
                   m.ScopeKindCode, m.CampaignId,
                   CASE WHEN v.SensitivityCode = 0 AND NOT EXISTS (
                       SELECT 1 FROM artifact_sensitivity a WHERE a.ArtifactKindCode = 6
                       AND lower(replace(a.ArtifactId, '-', '')) = lower(replace(m.Id, '-', '')))
                       THEN m.Content ELSE NULL END
            FROM annal_versions v
            JOIN annal_claims c ON c.ClaimId = v.ClaimId AND c.SubjectStoreCode = 1
            JOIN annal_heads h ON h.ClaimId = c.ClaimId
            JOIN saga_memories m ON m.Id = c.SubjectId
            WHERE v.VersionId = @version AND c.SubjectId = @memory
            """;

        Add(command, "@version", target.ExpectedVersionId);

        Add(command, "@memory", target.MemoryId);

        LongRestSnapshot snapshot;

        string? content;

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result<LoadedSnapshot>.Failure(new Error(ErrorCodes.LongRest.MissingClaim,
                    "The target does not identify a retained Saga claim version."));
            }

            content = reader.IsDBNull(22) ? null : reader.GetString(22);

            DateTimeOffset? retired = Time(reader, 13);

            // A retirement asserts no content. Bind its retained predecessor's asserted digest
            // without materializing protected plaintext or accepting an unproved caller digest.
            if (reader.IsDBNull(4))
            {
                return Result<LoadedSnapshot>.Failure(new Error(ErrorCodes.LongRest.Unavailable,
                    "The retained version has no verifiable content binding."));
            }

            string hash = Convert.ToHexString((byte[])reader[4]);

            Guid? campaign = GuidValue(reader, 8);

            if (reader.GetInt32(15) == 1
                && (reader.GetInt32(7) != reader.GetInt32(20) || campaign != GuidValue(reader, 21)))
            {
                return Result<LoadedSnapshot>.Failure(new Error(ErrorCodes.LongRest.StaleInput,
                    "The subject no longer carries its exact version's scope."));
            }

            snapshot = new LongRestSnapshot(target.MemoryId, reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                (AnnalContentHashFormat)reader.GetInt32(3), hash, (AnnalOrigin)reader.GetInt32(5), GuidValue(reader, 6),
                (SagaMemoryScopeKind)reader.GetInt32(7), campaign, (ContentSensitivity)reader.GetInt32(9),
                UtcInstantText.Parse(reader.GetString(10)), Time(reader, 11), UtcInstantText.Parse(reader.GetString(12)),
                retired, Time(reader, 14), reader.GetInt32(15) == 1, reader.GetInt32(18) == 1,
                reader.GetInt32(16) == 1, reader.GetInt32(17) == 1, reader.GetInt32(19) == 1, [], []);
        }

        List<LongRestDependency> dependencies = [];

        command.Parameters.Clear();

        Add(command, "@version", target.ExpectedVersionId);

        command.CommandText = "SELECT DependencyVersionId, RelationCode, Ordinal FROM annal_dependencies WHERE DependentVersionId = @version ORDER BY Ordinal LIMIT 17";

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                dependencies.Add(new LongRestDependency(reader.GetString(0), (AnnalDependencyRelation)reader.GetInt32(1), reader.GetInt32(2)));
            }
        }

        List<LongRestIncomingDependency> incoming = [];

        command.CommandText =
            """
            SELECT d.DependentVersionId, d.RelationCode
            FROM annal_dependencies d
            JOIN annal_heads h ON h.CurrentVersionId = d.DependentVersionId
            WHERE d.DependencyVersionId = @version
            ORDER BY d.DependentVersionId, d.RelationCode LIMIT 17
            """;

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                incoming.Add(new LongRestIncomingDependency(reader.GetString(0), (AnnalDependencyRelation)reader.GetInt32(1), true));
            }
        }

        if (dependencies.Count > LongRestPolicy.MaxTargets || incoming.Count > LongRestPolicy.MaxTargets)
        {
            return Result<LoadedSnapshot>.Failure(new Error(ErrorCodes.LongRest.Unavailable,
                "This dependency group exceeds the bounded canonical transformation surface."));
        }

        return new LoadedSnapshot(snapshot with { Dependencies = dependencies.ToArray(), IncomingDependencies = incoming.ToArray() }, content);
    }

    private static DateTimeOffset? Time(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : UtcInstantText.Parse(reader.GetString(ordinal));

    private static Guid? GuidValue(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static void Add(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value ?? DBNull.Value;

        command.Parameters.Add(parameter);
    }
}
