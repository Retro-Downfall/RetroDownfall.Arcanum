using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
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

            LongRestVerificationCounters.Transaction(deferred: true);

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
        LongRestWorkIdentity work, string unit, LongRestVerificationDepth depth, CancellationToken ct)
    {
        LongRestDiscoveryGeneration generation = await ReadSweepGenerationAsync(connection, transaction, work.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (generation.GenerationId != work.GenerationId || generation.Scope != work.Scope || generation.Fence != work.Fence
            || LongRestDiscoveryHash.Recipe(generation.Recipe).Value != work.RecipeDigest)
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenMetadataAsync(connection, transaction, generation, depth, ct).ConfigureAwait(false);

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
        LongRestDiscoveryGeneration generation, LongRestVerificationDepth depth, CancellationToken ct)
    {
        if (!generation.Published || generation.Phase is not (LongRestSweepPhase.Discover or LongRestSweepPhase.Adjudicate or LongRestSweepPhase.Completed))
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenMetadataAsync(connection, transaction, generation, depth, ct).ConfigureAwait(false);
    }

    internal static async Task ValidateCompletedCorpusMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSweepScope? partition, CancellationToken ct)
    {
        if (!generation.Published || generation.Phase != LongRestSweepPhase.Completed
            || (partition is { } exact && exact != generation.Scope))
        {
            throw new InvalidDataException();
        }

        // The set-based question comes first. A corpus that has outgrown the generation (a source eligible since it was captured, a
        // disposition still open) fails here in one statement, before the per-leaf pass that costs a read of every leaf, so a stale
        // candidate never pays for that pass. The verdict is unchanged: both proofs must hold.
        LongRestVerificationCounters.AntiJoinPass();

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT NOT EXISTS (SELECT 1 FROM long_rest_work_items WHERE GenerationId = $generation AND DispositionCode <> 2)
              AND NOT EXISTS (
                SELECT 1 FROM long_rest_sources source JOIN saga_memories memory ON memory.Id = source.MemoryId
                LEFT JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
                LEFT JOIN annal_heads head ON head.ClaimId = claim.ClaimId
                LEFT JOIN annal_versions version ON version.VersionId = head.CurrentVersionId
                WHERE memory.ScopeKindCode = $scope AND memory.CampaignId IS $campaign AND ({SourceOwnerEligible})
                  {(partition is null ? "" : $"AND ({PartitionEligibility})")}
                  AND ({SourceLifecycleEligible})
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

        // Adopting a completed generation's answer is a seam: every leaf, once.
        await VerifyFrozenMetadataAsync(connection, transaction, generation, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The metadata-only form of <see cref="VerifyFrozenSourcesAsync"/>, for callers that hold no capability and so cannot read protected
    /// content: the same set-based question, then every leaf's metadata at a seam or one rotating window of them at a step.
    /// </summary>
    /// <remarks>
    /// A seam proves every leaf in one statement, however many there are (<see cref="VerifyFrozenMetadataCorpusAsync"/>); a step reads the
    /// leaves of its window one by one, which is a bounded number whatever the corpus holds.
    /// </remarks>
    internal static async Task VerifyFrozenMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestVerificationDepth depth, CancellationToken ct)
    {
        if (!await FrozenSourceMetadataMatchesAsync(connection, transaction, generation, ct).ConfigureAwait(false))
        {
            throw new InvalidDataException();
        }

        if (depth != LongRestVerificationDepth.Step)
        {
            LongRestVerificationCounters.CorpusPass();

            await VerifyFrozenMetadataCorpusAsync(connection, transaction, generation, ct).ConfigureAwait(false);

            return;
        }

        LongRestVerificationCounters.ScrubWindow();

        foreach (string memory in await ReadScrubWindowAsync(connection, transaction, generation, ct).ConfigureAwait(false))
        {
            _ = await ReadFrozenMetadataLeafAsync(connection, transaction, generation, memory, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One leaf of a generation re-proved by its metadata alone: its snapshot and label read as the store holds them now, compared with what
    /// the leaf froze, inside SQLite where the manifests are concerned. A step's window and a unit's inputs are read this way, a bounded
    /// number of leaves; a seam reads every leaf in one statement instead (<see cref="VerifyFrozenMetadataCorpusAsync"/>), which this is the
    /// reference for.
    /// </summary>
    internal static async Task<LongRestSourceLeaf> ReadFrozenMetadataLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, CancellationToken ct)
    {
        LongRestVerificationCounters.MetadataLeafRead();

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

        LongRestSourceLeaf current = ProveMetadataLeaf(generation, snapshot.Value.Snapshot, label, frozen, digest, sequence, epoch, embedding);

        if (current.Snapshot.IsConsolidated != consolidated || current.Snapshot.IsTransformationOutput != output)
        {
            await VerifyOwnProjectionAsync(connection, transaction, generation, current, frozen, ct).ConfigureAwait(false);
        }

        return current;
    }

    /// <summary>
    /// The part of re-proving a leaf that needs nothing but what has been read: the leaf as the store describes it now, and whether the digest
    /// its generation froze is the digest of that leaf with the flags it was frozen with. The one body the per-leaf read and the set read share.
    /// </summary>
    private static LongRestSourceLeaf ProveMetadataLeaf(LongRestDiscoveryGeneration generation, LongRestSnapshot snapshot,
        ArtifactSensitivityLabel? label, LongRestSnapshot frozen, CovenantDigest digest, long sequence, long epoch, LongRestSourceEmbeddingBinding? embedding)
    {
        LongRestSourceLeaf current = new(snapshot, sequence, epoch, embedding, label?.Sensitivity ?? ContentSensitivity.None,
            label?.Provenance ?? GenerationProvenance.CreateExact([]), label is null ? [] : [label.LabelDigest], CanonicalLabel: label,
            SagaContentDigest: label?.ArtifactContentDigest, SagaRevision: label?.ArtifactRevision);

        LongRestSourceLeaf comparable = current with { Snapshot = frozen };

        if (LongRestDiscoveryHash.SourcePage(generation.Scope, [comparable]).Value != digest)
        {
            throw new InvalidDataException();
        }

        return current;
    }

    // The columns of a leaf, then whether the live rows a snapshot is made of all exist, then the dependencies and the incoming dependencies of its
    // version as JSON arrays of at most seventeen elements (one more than the bound, so an excess is seen), then the snapshot's columns, then
    // the label's. The snapshot and label columns are the ones the point reads select, defined once and read by the one decoder each has.
    private const int CorpusFound = 11;

    private const int CorpusDependencies = 12;

    private const int CorpusIncoming = 13;

    private const int CorpusSnapshot = 14;

    private const int CorpusLabel = CorpusSnapshot + SagaLongRestService.SnapshotColumnCount;

    /// <summary>
    /// The statement that reads every leaf of a generation beside the live rows it must still describe. It is the point reads of a leaf joined
    /// over the generation, so what it selects for a leaf is what those reads select for it, and every join is a lookup by key. A clean
    /// generation joins no label, because a clean leaf has none and the set-based question has already refused a clean leaf that has one.
    /// </summary>
    internal static readonly string FrozenMetadataCorpusSql = $"""
        SELECT leaf.MemoryId, leaf.VersionId, leaf.SourceDigest, leaf.InsertionSequence, leaf.MutationEpoch, leaf.EmbeddingDigest, leaf.EmbeddingDimensions,
            json_extract(leaf.SnapshotJson, '$.isConsolidated'), json_extract(leaf.SnapshotJson, '$.isTransformationOutput'), leaf.SnapshotJson, leaf.LabelJson,
            v.VersionId IS NOT NULL AND c.ClaimId IS NOT NULL AND h.ClaimId IS NOT NULL AND m.Id IS NOT NULL,
            (SELECT json_group_array(json_array(x.DependencyVersionId, x.RelationCode, x.Ordinal) ORDER BY x.Ordinal)
                FROM (SELECT DependencyVersionId, RelationCode, Ordinal FROM annal_dependencies WHERE DependentVersionId = v.VersionId ORDER BY Ordinal LIMIT 17) x),
            (SELECT json_group_array(json_array(y.DependentVersionId, y.RelationCode) ORDER BY y.DependentVersionId, y.RelationCode)
                FROM (SELECT d.DependentVersionId, d.RelationCode FROM annal_dependencies d JOIN annal_heads ih ON ih.CurrentVersionId = d.DependentVersionId
                    WHERE d.DependencyVersionId = v.VersionId ORDER BY d.DependentVersionId, d.RelationCode LIMIT 17) y),
            {SagaLongRestService.SnapshotColumns},
            {string.Join(", ", ArtifactSensitivityLedger.LabelColumnList.Split(", ").Select(static column => "label." + column))}
        FROM long_rest_discovery_leaves leaf
        LEFT JOIN annal_versions v ON v.VersionId = leaf.VersionId
        LEFT JOIN annal_claims c ON c.ClaimId = v.ClaimId AND c.SubjectStoreCode = 1 AND c.SubjectId = leaf.MemoryId
        LEFT JOIN annal_heads h ON h.ClaimId = c.ClaimId
        LEFT JOIN saga_memories m ON m.Id = c.SubjectId
        LEFT JOIN artifact_sensitivity label ON $partition IS NOT NULL AND label.ArtifactKindCode = 6 AND label.ArtifactId = upper(leaf.MemoryId)
        WHERE leaf.GenerationId = $generation
        ORDER BY leaf.MemoryId;
        """;

    /// <summary>
    /// Every leaf of a generation re-proved by its metadata, in a constant number of statements however many leaves there are.
    /// </summary>
    /// <remarks>
    /// <para>This is <see cref="ReadFrozenMetadataLeafAsync"/> for all of them at once, and it asks exactly what that does of each: the live
    /// rows must exist, the snapshot they make (with the flags the leaf was frozen with) and the label must be the manifests the leaf
    /// froze, byte for byte, and the digest the generation froze must be the digest of that leaf. Reading one leaf takes five statements
    /// and so a corpus of them took five for each leaf; here the live rows are joined to the leaves in one statement whose every join is a
    /// lookup by key, and the same comparison runs on each row as it is read. Nothing is cached and nothing is skipped: the digest is
    /// recomputed from what the rows say now.</para>
    /// <para>Two kinds of leaf still need more than the row. A leaf the sweep itself consolidated, or produced, no longer has the flags it
    /// was frozen with, and the receipt that explains the difference must be proved in full (<see cref="VerifyOwnProjectionAsync"/>), as the
    /// per-leaf read does; those leaves are proved after the statement has been read through, one at a time, so the work grows with the
    /// decisions the sweep applied and not with the corpus. And a protected leaf whose label the exact-spelling join does not find is handed
    /// to the per-leaf read, which makes the point read of the label and so decides it as it always did.</para>
    /// </remarks>
    internal static async Task VerifyFrozenMetadataCorpusAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken ct)
    {
        List<(LongRestSourceLeaf Live, LongRestSnapshot Frozen)> explained = [];

        List<string> exact = [];

        await using (SqliteCommand command = Command(connection, transaction, FrozenMetadataCorpusSql, ("$generation", Format(generation.GenerationId)),
            ("$partition", generation.Scope.ProtectedPartitionDigest?.Bytes), ("@materialize", 0)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string memory = reader.GetString(0);

                ArtifactSensitivityLabel? label = null;

                if (generation.Scope.ProtectedPartitionDigest is not null)
                {
                    if (reader.IsDBNull(CorpusLabel))
                    {
                        exact.Add(memory);

                        continue;
                    }

                    Result<ArtifactSensitivityLabel> decoded = ArtifactSensitivityLedger.Materialize(reader, CorpusLabel);

                    if (decoded.IsFailure)
                    {
                        throw new InvalidDataException();
                    }

                    label = decoded.Value;
                }

                LongRestVerificationCounters.MetadataLeafRead();

                if (!reader.GetBoolean(CorpusFound))
                {
                    throw new InvalidDataException();
                }

                Result<LongRestSnapshot> described = SagaLongRestService.SnapshotFrom(reader, CorpusSnapshot, memory);

                if (described.IsFailure)
                {
                    throw new InvalidDataException();
                }

                LongRestDependency[] dependencies = ReadDependencies(reader.GetString(CorpusDependencies));

                LongRestIncomingDependency[] incoming = ReadIncomingDependencies(reader.GetString(CorpusIncoming));

                if (dependencies.Length > LongRestPolicy.MaxTargets || incoming.Length > LongRestPolicy.MaxTargets)
                {
                    throw new InvalidDataException();
                }

                LongRestSnapshot snapshot = described.Value with { Dependencies = dependencies, IncomingDependencies = incoming };

                bool consolidated = reader.GetInt32(7) == 1;

                bool output = reader.GetInt32(8) == 1;

                LongRestSnapshot frozen = snapshot with { IsConsolidated = consolidated, IsTransformationOutput = output };

                if (!string.Equals(reader.GetString(9), JsonSerializer.Serialize(frozen, LongRestJsonContext.Default.LongRestSnapshot), StringComparison.Ordinal)
                    || !string.Equals(reader.IsDBNull(10) ? null : reader.GetString(10),
                        label is null ? null : JsonSerializer.Serialize(label, LongRestJsonContext.Default.ArtifactSensitivityLabel), StringComparison.Ordinal))
                {
                    throw new InvalidDataException();
                }

                LongRestSourceEmbeddingBinding? embedding = reader.IsDBNull(5) ? null : new(LongRestSourceEmbeddingKind.ObservedSaga, reader.GetInt32(6), new((byte[])reader[5]));

                LongRestSourceLeaf current = ProveMetadataLeaf(generation, snapshot, label, frozen, new((byte[])reader[2]), reader.GetInt64(3), reader.GetInt64(4), embedding);

                if (snapshot.IsConsolidated != consolidated || snapshot.IsTransformationOutput != output)
                {
                    explained.Add((current, frozen));
                }
            }
        }

        foreach ((LongRestSourceLeaf live, LongRestSnapshot frozen) in explained)
        {
            await VerifyOwnProjectionAsync(connection, transaction, generation, live, frozen, ct).ConfigureAwait(false);
        }

        foreach (string memory in exact)
        {
            _ = await ReadFrozenMetadataLeafAsync(connection, transaction, generation, memory, ct).ConfigureAwait(false);
        }
    }

    private static LongRestDependency[] ReadDependencies(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return [.. document.RootElement.EnumerateArray().Select(static element =>
            new LongRestDependency(element[0].GetString()!, (AnnalDependencyRelation)element[1].GetInt32(), element[2].GetInt32()))];
    }

    private static LongRestIncomingDependency[] ReadIncomingDependencies(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return [.. document.RootElement.EnumerateArray().Select(static element =>
            new LongRestIncomingDependency(element[0].GetString()!, (AnnalDependencyRelation)element[1].GetInt32(), true))];
    }

    private static string MetadataLeafJson(LongRestSourceLeaf leaf) => JsonSerializer.Serialize(leaf with { Content = null, Vector = [] }, LongRestJsonContext.Default.LongRestSourceLeaf);
}
