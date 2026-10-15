using System.Data.Common;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class SagaLongRestService
{
    /// <summary>
    /// The columns of one snapshot row, in the order <see cref="SnapshotFrom"/> reads them. They are one definition, shared by the point read
    /// of a version and by the set read of a generation's leaves, so the two cannot describe a snapshot differently. They name the aliases
    /// <c>v</c> (the version), <c>m</c> (the memory) and <c>h</c> (the head) and the parameter <c>@materialize</c>.
    /// </summary>
    internal const string SnapshotColumns =
        """
        v.ClaimId, v.VersionId, v.Revision, v.ContentHashFormatCode,
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
               CASE WHEN @materialize = 1 AND v.SensitivityCode = 0 AND NOT EXISTS (
                   SELECT 1 FROM artifact_sensitivity a WHERE a.ArtifactKindCode = 6
                   AND lower(replace(a.ArtifactId, '-', '')) = lower(replace(m.Id, '-', '')))
                   THEN m.Content ELSE NULL END
        """;

    /// <summary>How many columns <see cref="SnapshotColumns"/> selects.</summary>
    internal const int SnapshotColumnCount = 23;

    // Metadata and labels are read before plaintext. A protected row never materializes Content through this ordinary policy path, even when
    // its historical Annals sensitivity was None. The label comparison is the loose, spelling-insensitive one on purpose: a mis-spelled
    // protected label must still count.
    internal const string SnapshotSql =
        "SELECT " + SnapshotColumns +
        """

        FROM annal_versions v
        JOIN annal_claims c ON c.ClaimId = v.ClaimId AND c.SubjectStoreCode = 1
        JOIN annal_heads h ON h.ClaimId = c.ClaimId
        JOIN saga_memories m ON m.Id = c.SubjectId
        WHERE v.VersionId = @version AND c.SubjectId = @memory
        """;

    internal static async Task<Result<LoadedSnapshot>> ReadSnapshotAsync(
        DbConnection connection, DbTransaction transaction, LongRestTarget target, CancellationToken cancellationToken, bool materializeContent = true)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = SnapshotSql;

        Add(command, "@materialize", materializeContent ? 1 : 0);

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

            content = reader.IsDBNull(SnapshotColumnCount - 1) ? null : reader.GetString(SnapshotColumnCount - 1);

            Result<LongRestSnapshot> built = SnapshotFrom(reader, 0, target.MemoryId);

            if (built.IsFailure)
            {
                return Result<LoadedSnapshot>.Failure(built.Error);
            }

            snapshot = built.Value;
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

    /// <summary>
    /// The snapshot that the <see cref="SnapshotColumns"/> of the reader's current row describe, without its dependencies, or why that row
    /// binds none. <paramref name="first"/> is the ordinal of the row's first snapshot column, so the one definition serves a row of its own
    /// and a row that carries other columns before it.
    /// </summary>
    internal static Result<LongRestSnapshot> SnapshotFrom(DbDataReader reader, int first, string memoryId)
    {
        DateTimeOffset? retired = Time(reader.IsDBNull(first + 13) ? null : reader.GetString(first + 13));

        // A retirement asserts no content. Bind its retained predecessor's asserted digest
        // without materializing protected plaintext or accepting an unproved caller digest.
        if (reader.IsDBNull(first + 4))
        {
            return Result<LongRestSnapshot>.Failure(new Error(ErrorCodes.LongRest.Unavailable,
                "The retained version has no verifiable content binding."));
        }

        string hash = Convert.ToHexString((byte[])reader[first + 4]);

        Guid? campaign = GuidValue(reader.IsDBNull(first + 8) ? null : reader.GetString(first + 8));

        if (reader.GetInt32(first + 15) == 1
            && (reader.GetInt32(first + 7) != reader.GetInt32(first + 20)
                || campaign != GuidValue(reader.IsDBNull(first + 21) ? null : reader.GetString(first + 21))))
        {
            return Result<LongRestSnapshot>.Failure(new Error(ErrorCodes.LongRest.StaleInput,
                "The subject no longer carries its exact version's scope."));
        }

        return new LongRestSnapshot(memoryId, reader.GetString(first), reader.GetString(first + 1), reader.GetInt32(first + 2),
            (AnnalContentHashFormat)reader.GetInt32(first + 3), hash, (AnnalOrigin)reader.GetInt32(first + 5),
            GuidValue(reader.IsDBNull(first + 6) ? null : reader.GetString(first + 6)),
            (SagaMemoryScopeKind)reader.GetInt32(first + 7), campaign, (ContentSensitivity)reader.GetInt32(first + 9),
            UtcInstantText.Parse(reader.GetString(first + 10)), Time(reader.IsDBNull(first + 11) ? null : reader.GetString(first + 11)),
            UtcInstantText.Parse(reader.GetString(first + 12)), retired, Time(reader.IsDBNull(first + 14) ? null : reader.GetString(first + 14)),
            reader.GetInt32(first + 15) == 1, reader.GetInt32(first + 18) == 1,
            reader.GetInt32(first + 16) == 1, reader.GetInt32(first + 17) == 1, reader.GetInt32(first + 19) == 1, [], []);
    }

    private static DateTimeOffset? Time(string? text) => text is null ? null : UtcInstantText.Parse(text);

    private static Guid? GuidValue(string? text) => text is null ? null : Guid.Parse(text);

    private static void Add(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value ?? DBNull.Value;

        command.Parameters.Add(parameter);
    }
}
