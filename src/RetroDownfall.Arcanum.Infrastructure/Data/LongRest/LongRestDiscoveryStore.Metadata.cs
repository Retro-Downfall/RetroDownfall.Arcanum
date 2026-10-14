using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    public async Task<Result<LongRestDiscoveryGeneration?>> ReadGenerationMetadataAsync(Guid operationId, CancellationToken ct)
    {
        if (operationId == Guid.Empty)
        {
            return Result<LongRestDiscoveryGeneration?>.Failure(new Error(ErrorCodes.LongRest.InvalidRequest, "Generation metadata requires an exact owning operation."));
        }

        try
        {
            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            await using SqliteCommand owner = Command(connection, transaction,
                "SELECT count(*) FROM LongRunningOperations WHERE Id = $id AND Kind = 'long-rest-sweep';", ("$id", operationId.ToString("N")));

            if ((long)(await owner.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1)
            {
                throw new InvalidDataException();
            }

            LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction,
                "OperationId = $id", operationId.ToString("N"), ct).ConfigureAwait(false);

            if (generation is null)
            {
                owner.CommandText = "SELECT count(*) FROM long_rest_sweeps WHERE OperationId = $id AND GenerationId IS NOT NULL;";

                if ((long)(await owner.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0)
                {
                    throw new InvalidDataException();
                }

                return Result<LongRestDiscoveryGeneration?>.Success(null);
            }

            await using SqliteCommand sweep = Command(connection, transaction, """
                SELECT ConfigurationJson FROM long_rest_sweeps WHERE OperationId = $operation AND GenerationId = $generation
                  AND ScopeKindCode = $scope AND CampaignId IS $campaign AND PartitionDigest IS $partition;
                """, ("$operation", operationId.ToString("N")), ("$generation", Format(generation.GenerationId)),
                ("$scope", (int)generation.Scope.Kind), ("$campaign", generation.Scope.CampaignId is { } campaign ? Format(campaign) : null),
                ("$partition", generation.Scope.ProtectedPartitionDigest?.Bytes));

            string? stamp = await sweep.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

            if (stamp is null || JsonSerializer.Deserialize(stamp, LongRestJsonContext.Default.LongRestConfigurationStamp) != generation.Fence.Configuration
                || generation.OperationId != operationId || generation.GenerationId == Guid.Empty
                || !Enum.IsDefined(generation.Phase) || generation.Revision < 0 || generation.FoldGroup < 0
                || generation.FoldLayer is < 0 or > LongRestDiscoveryLimits.HierarchyLevels)
            {
                throw new InvalidDataException();
            }

            return Result<LongRestDiscoveryGeneration?>.Success(generation);
        }
        catch (Exception exception) when (StorageFailure(exception) || exception is InvalidOperationException)
        {
            return Result<LongRestDiscoveryGeneration?>.Failure(new Error(ErrorCodes.LongRest.StaleInput, "The exact operation-owned generation metadata could not be proved."));
        }
    }

    /// <summary>Metadata consistency only. This grants no capability, plaintext, payload or send authority.</summary>
    internal static async Task ValidateAttemptSourceMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestWorkIdentity work, string unit, CancellationToken ct)
    {
        LongRestDiscoveryGeneration generation = await ReadSweepGenerationAsync(connection, transaction, work.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (generation.GenerationId != work.GenerationId || generation.Scope != work.Scope || generation.Fence != work.Fence
            || LongRestDiscoveryHash.Recipe(generation.Recipe).Value != work.RecipeDigest)
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenMetadataAsync(connection, transaction, generation, ct).ConfigureAwait(false);

        if (work.Kind != LongRestWorkKind.Anchor)
        {
            if (work.Kind != LongRestWorkKind.HierarchyFold || work.ItemId != "fold:" + Math.Max(1, generation.FoldLayer).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ":" + generation.FoldGroup.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException();
            }

            return;
        }

        ImmutableArray<LongRestSourceLeaf>.Builder inputs = ImmutableArray.CreateBuilder<LongRestSourceLeaf>();

        await using SqliteCommand command = Command(connection, transaction, "SELECT Ordinal, MemoryId, VersionId FROM long_rest_work_inputs WHERE WorkItemId = $unit ORDER BY Ordinal LIMIT 17;", ("$unit", unit));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            LongRestSourceLeaf current = await ReadFrozenMetadataLeafAsync(connection, transaction, generation, reader.GetString(1), ct).ConfigureAwait(false);

            // Compare inside SQLite. No persisted leaf JSON, Content or Vector crosses this port.
            await using SqliteCommand manifest = Command(connection, transaction, "SELECT count(*) FROM long_rest_work_inputs WHERE WorkItemId = $unit AND Ordinal = $ordinal AND json_set(SourceEvidenceJson, '$.content', NULL, '$.vector', json('[]')) = $json;",
                ("$unit", unit), ("$ordinal", reader.GetInt32(0)), ("$json", MetadataLeafJson(current)));

            if (inputs.Count >= LongRestPolicy.MaxTargets || reader.GetInt32(0) != inputs.Count + 1 || current.Snapshot.VersionId != reader.GetString(2)
                || (long)(await manifest.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1)
            {
                throw new InvalidDataException();
            }

            inputs.Add(current);
        }

        if (inputs.Count < 2 || LongRestDiscoveryHash.SourcePage(work.Scope, inputs.ToImmutable()).Value != work.SourceDigest)
        {
            throw new InvalidDataException();
        }
    }

    /// <summary>Current metadata prerequisite to reading retained adjudication content; never a capability.</summary>
    internal static async Task ValidateSweepEvidenceMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        if (!generation.Published || generation.Phase is not (LongRestSweepPhase.Discover or LongRestSweepPhase.Adjudicate or LongRestSweepPhase.Completed))
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenMetadataAsync(connection, transaction, generation, ct).ConfigureAwait(false);
    }

    internal static async Task ValidateCompletedCorpusMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSweepScope? partition, CancellationToken ct)
    {
        if (!generation.Published || generation.Phase != LongRestSweepPhase.Completed
            || (partition is { } exact && exact != generation.Scope))
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenMetadataAsync(connection, transaction, generation, ct).ConfigureAwait(false);

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT NOT EXISTS (SELECT 1 FROM long_rest_work_items WHERE GenerationId = $generation AND DispositionCode <> 2)
              AND NOT EXISTS (
                SELECT 1 FROM long_rest_sources source JOIN saga_memories memory ON memory.Id = source.MemoryId
                LEFT JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
                LEFT JOIN annal_heads head ON head.ClaimId = claim.ClaimId
                LEFT JOIN annal_versions version ON version.VersionId = head.CurrentVersionId
                WHERE memory.ScopeKindCode = $scope AND memory.CampaignId IS $campaign AND ({SourceOwnerEligible})
                  {(partition is null ? "" : $"AND ({PartitionEligibility})")}
                  AND (head.CurrentVersionId IS NULL OR ({SourceLifecycleEligible}))
                  AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_leaves leaf WHERE leaf.GenerationId = $generation
                    AND leaf.MemoryId = source.MemoryId AND leaf.VersionId = head.CurrentVersionId
                    AND leaf.InsertionSequence = source.InsertionSequence AND leaf.MutationEpoch = source.MutationEpoch));
            """, ("$generation", Format(generation.GenerationId)), ("$scope", (int)generation.Scope.Kind),
            ("$campaign", generation.Scope.CampaignId is { } campaign ? Format(campaign) : null),
            ("$partition", partition?.ProtectedPartitionDigest?.Bytes));

        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1)
        {
            throw new InvalidDataException();
        }
    }

    internal static async Task VerifyFrozenMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        if (!await FrozenSourceMetadataMatchesAsync(connection, transaction, generation, ct).ConfigureAwait(false))
        {
            throw new InvalidDataException();
        }

        string? cursor = null;

        while (true)
        {
            List<string> page = [];

            await using (SqliteCommand command = Command(connection, transaction, "SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation AND ($cursor IS NULL OR MemoryId > $cursor) ORDER BY MemoryId LIMIT 128;", ("$generation", Format(generation.GenerationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    page.Add(reader.GetString(0));
                }
            }

            if (page.Count == 0)
            {
                return;
            }

            foreach (string memory in page)
            {
                _ = await ReadFrozenMetadataLeafAsync(connection, transaction, generation, memory, ct).ConfigureAwait(false);
            }

            cursor = page[^1];
        }
    }

    private static async Task<LongRestSourceLeaf> ReadFrozenMetadataLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, CancellationToken ct)
    {
        CovenantDigest digest;

        LongRestSourceEmbeddingBinding? embedding;

        long sequence;

        long epoch;

        string version;

        bool consolidated;

        bool output;

        await using (SqliteCommand command = Command(connection, transaction, "SELECT VersionId, SourceDigest, InsertionSequence, MutationEpoch, EmbeddingDigest, EmbeddingDimensions, json_extract(SnapshotJson, '$.isConsolidated'), json_extract(SnapshotJson, '$.isTransformationOutput') FROM long_rest_discovery_leaves WHERE GenerationId = $generation AND MemoryId = $memory;", ("$generation", Format(generation.GenerationId)), ("$memory", memory)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new InvalidDataException();
            }

            version = reader.GetString(0);

            digest = new((byte[])reader[1]);

            sequence = reader.GetInt64(2);

            epoch = reader.GetInt64(3);

            embedding = reader.IsDBNull(4) ? null : new(LongRestSourceEmbeddingKind.ObservedSaga, reader.GetInt32(5), new((byte[])reader[4]));

            consolidated = reader.GetInt32(6) == 1;

            output = reader.GetInt32(7) == 1;
        }

        var snapshot = await SagaLongRestService.ReadSnapshotAsync(connection, transaction, new(memory, version, new string('0', 64)), ct, false).ConfigureAwait(false);

        if (snapshot.IsFailure)
        {
            throw new InvalidDataException();
        }

        ArtifactSensitivityLabel? label = null;

        if (generation.Scope.ProtectedPartitionDigest is not null)
        {
            var result = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction, SensitiveArtifactKind.Saga, Guid.Parse(memory), ct).ConfigureAwait(false);

            if (result.IsFailure || result.Value is null)
            {
                throw new InvalidDataException();
            }

            label = result.Value;
        }

        LongRestSnapshot frozen = snapshot.Value.Snapshot with { IsConsolidated = consolidated, IsTransformationOutput = output };

        // Full manifests are compared to canonical current metadata inside SQLite, never selected.
        await using SqliteCommand retained = Command(connection, transaction, "SELECT count(*) FROM long_rest_discovery_leaves WHERE GenerationId = $generation AND MemoryId = $memory AND SnapshotJson = $snapshot AND LabelJson IS $label;",
            ("$generation", Format(generation.GenerationId)), ("$memory", memory), ("$snapshot", JsonSerializer.Serialize(frozen, LongRestJsonContext.Default.LongRestSnapshot)),
            ("$label", label is null ? null : JsonSerializer.Serialize(label, LongRestJsonContext.Default.ArtifactSensitivityLabel)));

        if ((long)(await retained.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1)
        {
            throw new InvalidDataException();
        }

        LongRestSourceLeaf current = new(snapshot.Value.Snapshot, sequence, epoch, embedding, label?.Sensitivity ?? ContentSensitivity.None,
            label?.Provenance ?? GenerationProvenance.CreateExact([]), label is null ? [] : [label.LabelDigest], CanonicalLabel: label,
            SagaContentDigest: label?.ArtifactContentDigest, SagaRevision: label?.ArtifactRevision);

        LongRestSourceLeaf comparable = current with { Snapshot = frozen };

        if (LongRestDiscoveryHash.SourcePage(generation.Scope, [comparable]).Value != digest)
        {
            throw new InvalidDataException();
        }

        if (current.Snapshot.IsConsolidated != consolidated || current.Snapshot.IsTransformationOutput != output)
        {
            await VerifyOwnProjectionAsync(connection, transaction, generation, current, frozen, ct).ConfigureAwait(false);
        }

        return current;
    }

    private static string MetadataLeafJson(LongRestSourceLeaf leaf) => JsonSerializer.Serialize(leaf with { Content = null, Vector = [] }, LongRestJsonContext.Default.LongRestSourceLeaf);
}
