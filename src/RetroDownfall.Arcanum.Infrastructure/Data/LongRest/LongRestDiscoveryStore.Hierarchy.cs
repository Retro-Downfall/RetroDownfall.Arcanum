using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    public async Task<Result<LongRestHierarchyWork?>> ReadNextFoldAsync(Guid generationId,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        try
        {
            SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction, "GenerationId = $id", Format(generationId), cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.Phase is not (LongRestSweepPhase.Hierarchy or LongRestSweepPhase.Publish))
            {
                return Refuse<LongRestHierarchyWork?>(ErrorCodes.LongRest.StaleInput, "The hierarchy generation is no longer current.");
            }

            Result authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

            if (authority.IsFailure)
            {
                return Result<LongRestHierarchyWork?>.Failure(authority.Error);
            }

            // A fold read is a step: the frontier page it plans from is read in full just below, so every input of the fold is re-proved here.
            await VerifyFrozenSourcesAsync(connection, transaction, generation, capability, LongRestVerificationDepth.Step, cancellationToken).ConfigureAwait(false);

            // Counted as the plan is walked, published only once the cursor it moved is durable.
            long oversize = 0;

            while (generation.Phase == LongRestSweepPhase.Hierarchy)
            {
                int layer = Math.Max(1, generation.FoldLayer);

                long page = generation.FoldGroup / LongRestDiscoveryLimits.SourcePage;

                int slot = (int)(generation.FoldGroup % LongRestDiscoveryLimits.SourcePage);

                ImmutableArray<LongRestHierarchyInput> inputs = await ReadFrontierPageAsync(connection, transaction, generation,
                    layer, page, capability, cancellationToken).ConfigureAwait(false);

                if (inputs.IsEmpty)
                {
                    await using SqliteCommand count = Command(connection, transaction,
                        "SELECT count(*) FROM long_rest_discovery_nodes WHERE GenerationId = $generation AND Layer = $layer;",
                        ("$generation", Format(generationId)), ("$layer", layer));

                    bool progressed = (long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;

                    generation = await MoveHierarchyCursorAsync(connection, transaction, generation,
                        progressed && layer < LongRestDiscoveryLimits.HierarchyLevels ? layer + 1 : layer, 0,
                        progressed && layer < LongRestDiscoveryLimits.HierarchyLevels ? LongRestSweepPhase.Hierarchy : LongRestSweepPhase.Publish,
                        cancellationToken).ConfigureAwait(false);

                    continue;
                }

                Result<ImmutableArray<ImmutableArray<LongRestHierarchyChild>>> planned = LongRestHierarchy.PlanPage(inputs, layer,
                    LongRestDiscoveryHash.Recipe(generation.Recipe).Value, generation.Recipe.ProviderInputUtf8Bytes, cancellationToken);

                if (planned.IsFailure)
                {
                    return Result<LongRestHierarchyWork?>.Failure(planned.Error);
                }

                if (slot >= planned.Value.Length)
                {
                    generation = await MoveHierarchyCursorAsync(connection, transaction, generation, layer,
                        checked((page + 1) * LongRestDiscoveryLimits.SourcePage), LongRestSweepPhase.Hierarchy, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                ImmutableArray<LongRestHierarchyChild> children = planned.Value[slot];

                if (children.Length == 1)
                {
                    // A carry advances only durable planning coordinates. Its actual node, layer,
                    // vector and original paid evidence remain untouched. A child no request can hold is carried the same way,
                    // and is counted the one time it first reaches a plan, not at every layer it is carried through.
                    if (children[0].Layer == layer - 1 && LongRestHierarchy.IsOversize(children[0], generation.Recipe.ProviderInputUtf8Bytes))
                    {
                        oversize++;
                    }

                    generation = await MoveHierarchyCursorAsync(connection, transaction, generation, layer,
                        checked(generation.FoldGroup + 1), LongRestSweepPhase.Hierarchy, cancellationToken).ConfigureAwait(false);

                    continue;
                }

                LongRestHierarchyFold fold = LongRestHierarchyBuilder.CreateFold(generation, layer, generation.FoldGroup, children);

                authority = await ValidateFoldAuthorityAsync(fold.Work, capability, cancellationToken).ConfigureAwait(false);

                if (authority.IsFailure)
                {
                    return Result<LongRestHierarchyWork?>.Failure(authority.Error);
                }

                LongRestAcceptedHierarchySummary? accepted = await ReadAcceptedSummaryAsync(connection, transaction, fold, generation.Recipe, cancellationToken).ConfigureAwait(false);

                await BindCurrentWorkAsync(connection, transaction, fold.Work, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                PublishOversize(oversize);

                return Result<LongRestHierarchyWork?>.Success(new(fold, accepted));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            PublishOversize(oversize);

            return Result<LongRestHierarchyWork?>.Success(null);
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestHierarchyWork?>(ErrorCodes.LongRest.Unavailable, "The hierarchy plan could not retain its exact source and child bindings.");
        }
    }

    private static void PublishOversize(long oversize)
    {
        if (oversize > 0)
        {
            ArcanumMetrics.LongRestHierarchyOversizeChildrenTotal.Add(oversize);
        }
    }

    public async Task<Result<LongRestDiscoveryGeneration>> PublishAsync(Guid generationId, long expectedRevision,
        LongRestSweepFence fence, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        try
        {
            SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction, "GenerationId = $id", Format(generationId), cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.Revision != expectedRevision || generation.Fence != fence || generation.Phase != LongRestSweepPhase.Publish)
            {
                return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "Only the exact fully disposed hierarchy can publish.");
            }

            Result authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

            if (authority.IsFailure)
            {
                return Result<LongRestDiscoveryGeneration>.Failure(authority.Error);
            }

            // Publishing closes the build and opens discovery on the corpus as it stands: a seam, so every leaf is re-proved in full.
            await VerifyFrozenSourcesAsync(connection, transaction, generation, capability, LongRestVerificationDepth.Seam, cancellationToken).ConfigureAwait(false);

            await using SqliteCommand proof = Command(connection, transaction, """
                SELECT NOT EXISTS (
                    SELECT 1 FROM long_rest_discovery_leaves leaf WHERE leaf.GenerationId = $generation AND
                        (SELECT count(*) FROM long_rest_discovery_nodes node WHERE node.GenerationId = leaf.GenerationId AND node.Layer = 0 AND node.MemoryId = leaf.MemoryId) <> 1)
                  AND NOT EXISTS (
                    SELECT 1 FROM long_rest_discovery_nodes node WHERE node.GenerationId = $generation AND (
                        (node.Layer = 0 AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_leaves leaf WHERE leaf.GenerationId = node.GenerationId AND leaf.MemoryId = node.MemoryId))
                        OR (node.Layer > 0 AND (SELECT count(*) FROM long_rest_discovery_edges edge WHERE edge.GenerationId = node.GenerationId AND edge.ParentNodeId = node.NodeId) NOT BETWEEN 2 AND 16)))
                  AND NOT EXISTS (
                    SELECT 1 FROM long_rest_discovery_edges edge LEFT JOIN long_rest_discovery_nodes parent ON parent.NodeId = edge.ParentNodeId
                    LEFT JOIN long_rest_discovery_nodes child ON child.NodeId = edge.ChildNodeId
                    WHERE edge.GenerationId = $generation AND (parent.NodeId IS NULL OR child.NodeId IS NULL OR parent.GenerationId <> $generation
                        OR child.GenerationId <> $generation OR child.Layer >= parent.Layer OR child.Layer < 0))
                  AND NOT EXISTS (SELECT 1 FROM long_rest_work_items WHERE GenerationId = $generation AND KindCode = 2 AND DispositionCode = 1);
                """, ("$generation", Format(generationId)));

            if ((long)(await proof.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 1)
            {
                return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The forest does not cover each captured leaf exactly once with completed folds.");
            }

            string? rootCursor = null;

            while (true)
            {
                List<string> roots = [];

                await using (SqliteCommand rootPage = Command(connection, transaction, """
                    SELECT node.NodeId FROM long_rest_discovery_nodes node WHERE node.GenerationId = $generation
                        AND ($cursor IS NULL OR node.NodeId > $cursor)
                        AND NOT EXISTS (SELECT 1 FROM long_rest_discovery_edges edge
                            WHERE edge.GenerationId = node.GenerationId AND edge.ChildNodeId = node.NodeId)
                    ORDER BY node.NodeId LIMIT 128;
                    """, ("$generation", Format(generationId)), ("$cursor", rootCursor)))
                {
                    await using SqliteDataReader reader = await rootPage.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        roots.Add(reader.GetString(0));
                    }
                }

                if (roots.Count == 0)
                {
                    break;
                }

                foreach (string root in roots)
                {
                    _ = await ReadHierarchyNodeAsync(connection, transaction, generation, Guid.Parse(root), capability, cancellationToken).ConfigureAwait(false);
                }

                rootCursor = roots[^1];
            }

            authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

            if (authority.IsFailure)
            {
                return Result<LongRestDiscoveryGeneration>.Failure(authority.Error);
            }

            await using SqliteCommand publish = Command(connection, transaction, """
                UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1, Revision = Revision + 1
                WHERE GenerationId = $generation AND Revision = $revision AND PhaseCode = 3 AND Published = 0;
                """, ("$generation", Format(generationId)), ("$revision", expectedRevision));

            if (await publish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return generation with { Phase = LongRestSweepPhase.Discover, Published = true, Revision = checked(expectedRevision + 1) };
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.Unavailable, "The complete hierarchy could not be published atomically.");
        }
    }

    private static async Task<ImmutableArray<LongRestHierarchyInput>> ReadFrontierPageAsync(SqliteConnection connection,
        SqliteTransaction transaction, LongRestDiscoveryGeneration generation, int layer, long page,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        List<Guid> nodes = [];

        // Reconstruct the frontier at this layer's start. Parents created in THIS layer must not
        // shrink the paginated input and skip children on a later page or after restart.
        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT node.NodeId FROM long_rest_discovery_nodes node
            WHERE node.GenerationId = $generation AND node.Layer < $layer AND NOT EXISTS (
                SELECT 1 FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes parent ON parent.NodeId = edge.ParentNodeId
                WHERE edge.GenerationId = node.GenerationId AND edge.ChildNodeId = node.NodeId AND parent.Layer < $layer)
            ORDER BY node.Layer, CASE WHEN node.Layer = 0 THEN node.MemoryId ELSE '' END, node.GroupOrdinal, node.NodeId LIMIT 128 OFFSET $offset;
            """, ("$generation", Format(generation.GenerationId)), ("$layer", layer), ("$offset", checked(page * LongRestDiscoveryLimits.SourcePage))))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodes.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        ImmutableArray<LongRestHierarchyInput>.Builder inputs = ImmutableArray.CreateBuilder<LongRestHierarchyInput>(nodes.Count);

        foreach (Guid node in nodes)
        {
            inputs.Add(await ReadHierarchyNodeAsync(connection, transaction, generation, node, capability, cancellationToken).ConfigureAwait(false));
        }

        return inputs.MoveToImmutable();
    }

    private static async Task<LongRestHierarchyInput> ReadHierarchyNodeAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, Guid nodeId, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        int layer;

        long group;

        string? memory;

        CovenantDigest membership;

        CovenantDigest source;

        CovenantDigest? summaryDigest;

        ContentSensitivity sensitivity;

        GenerationProvenance provenance;

        long? summaryBytes;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT Layer, GroupOrdinal, MemoryId, MembershipDigest, SourceDigest, length(CAST(Summary AS BLOB)), SummaryDigest,
                SensitivityCode, ProvenanceJson, PartitionDigest, Revision
            FROM long_rest_discovery_nodes WHERE GenerationId = $generation AND NodeId = $node AND RecipeDigest = $recipe;
            """, ("$generation", Format(generation.GenerationId)), ("$node", Format(nodeId)), ("$recipe", LongRestDiscoveryHash.Recipe(generation.Recipe).Value.Bytes)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException();
            }

            layer = reader.GetInt32(0);

            group = reader.GetInt64(1);

            memory = reader.IsDBNull(2) ? null : reader.GetString(2);

            membership = new((byte[])reader[3]);

            source = new((byte[])reader[4]);

            summaryBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5);

            summaryDigest = reader.IsDBNull(6) ? null : new CovenantDigest((byte[])reader[6]);

            sensitivity = (ContentSensitivity)reader.GetInt32(7);

            provenance = reader.IsDBNull(8) ? GenerationProvenance.CreateExact([])
                : JsonSerializer.Deserialize(reader.GetString(8), LongRestJsonContext.Default.GenerationProvenance) ?? throw new InvalidDataException();

            CovenantDigest? storedPartition = reader.IsDBNull(9) ? null : new CovenantDigest((byte[])reader[9]);

            if (reader.GetInt64(10) != 0 || storedPartition != generation.Scope.ProtectedPartitionDigest
                || sensitivity != (storedPartition is null ? ContentSensitivity.None : ContentSensitivity.CovenantDerived)
                || (storedPartition is null ? !reader.IsDBNull(8) : provenance.IsEmpty))
            {
                throw new InvalidDataException();
            }
        }

        if (layer == 0)
        {
            VerifiedFrozenLeaf verified = await ReadVerifiedFrozenLeafAsync(connection, transaction, generation, memory ?? throw new InvalidDataException(), capability, cancellationToken).ConfigureAwait(false);

            LongRestSourceLeaf leaf = verified.Leaf;

            CovenantDigest expected = verified.OriginalSourceDigest;

            if (expected != source || expected != membership || leaf.Sensitivity != sensitivity || !leaf.Provenance.Equals(provenance))
            {
                throw new InvalidDataException();
            }

            return new(new(nodeId, layer, group, membership, source, leaf.Content!, leaf.Sensitivity, leaf.Provenance, leaf.LabelDigests, [leaf.Target]), leaf.Vector);
        }

        if (layer is < 1 or > 6 || summaryBytes is null or < 1 or > LongRestDiscoveryLimits.SummaryUtf8Bytes || summaryDigest is null)
        {
            throw new InvalidDataException();
        }

        ArtifactSensitivityLabel? label = null;

        await using (SqliteCommand manifest = Command(connection, transaction, """
            SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16
                AND lower(replace(ArtifactId, '-', '')) = lower(replace($node, '-', ''));
            """, ("$node", Format(nodeId))))
        {
            long count = (long)(await manifest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            if (count != (generation.Scope.ProtectedPartitionDigest is null ? 0 : 1))
            {
                throw new InvalidDataException();
            }
        }

        if (generation.Scope.ProtectedPartitionDigest is { } partition)
        {
            Result<ArtifactSensitivityLabel?> loaded = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
                SensitiveArtifactKind.LongRestDiscovery, nodeId, cancellationToken).ConfigureAwait(false);

            label = loaded.IsSuccess ? loaded.Value : null;

            if (label is null || label.SensitivityDigest != partition || label.ArtifactContentDigest != summaryDigest
                || label.ArtifactRevision != 0 || label.CampaignId != generation.Scope.CampaignId || label.SessionId is not null
                || label.Sensitivity != sensitivity || !label.Provenance.Equals(provenance))
            {
                throw new InvalidDataException();
            }
        }

        string summary;

        await using (SqliteCommand content = Command(connection, transaction,
            "SELECT Summary FROM long_rest_discovery_nodes WHERE NodeId = $node;", ("$node", Format(nodeId))))
        {
            summary = (string)(await content.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (summaryDigest != DerivedArtifactContentDigest.ForText(summary))
        {
            throw new InvalidDataException();
        }

        await using SqliteCommand vectorCommand = Command(connection, transaction, """
            SELECT Vector, ValueDigest, PaidEvidenceJson FROM long_rest_discovery_embeddings
            WHERE NodeId = $node AND Provider = $provider AND Model = $model AND Dimensions = $dimensions;
            """, ("$node", Format(nodeId)), ("$provider", generation.Recipe.EmbeddingProvider), ("$model", generation.Recipe.EmbeddingModel),
            ("$dimensions", generation.Recipe.EmbeddingDimensions));

        byte[] bytes;

        CovenantDigest value;

        LongRestPaidEvidence paid;

        await using (SqliteDataReader reader = await vectorCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException();
            }

            bytes = (byte[])reader[0];

            value = new((byte[])reader[1]);

            paid = (JsonSerializer.Deserialize(reader.GetString(2), LongRestJsonContext.Default.LongRestStoredPaid)
                ?? throw new InvalidDataException()).ToDomain();
        }

        if (bytes.Length != checked(generation.Recipe.EmbeddingDimensions * sizeof(float)) || new CovenantDigest(SHA256.HashData(bytes)) != value)
        {
            throw new InvalidDataException();
        }

        float[] vector = EmbeddingBlobCodec.Decode(bytes);

        if (vector.Any(component => !float.IsFinite(component)) || (EmbeddingBlobCodec.NormSquared(vector) <= 0 || !double.IsFinite(EmbeddingBlobCodec.NormSquared(vector))))
        {
            throw new InvalidDataException();
        }

        List<Guid> childIds = await ReadChildNodesAsync(connection, transaction, generation.GenerationId, nodeId, cancellationToken).ConfigureAwait(false);

        if (childIds.Count < 2)
        {
            throw new InvalidDataException();
        }

        ImmutableArray<LongRestHierarchyChild>.Builder children = ImmutableArray.CreateBuilder<LongRestHierarchyChild>(childIds.Count);

        foreach (Guid childId in childIds)
        {
            children.Add((await ReadHierarchyNodeAsync(connection, transaction, generation, childId, capability, cancellationToken).ConfigureAwait(false)).Child);
        }

        LongRestHierarchyFold current = LongRestHierarchyBuilder.CreateFold(generation, layer, group, children.MoveToImmutable());

        LongRestAcceptedHierarchySummary accepted = await ReadAcceptedSummaryAsync(connection, transaction, current, generation.Recipe, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        if (accepted.NodeId != nodeId || accepted.Summary != summary || current.MembershipDigest != membership || current.Work.SourceDigest != source
            || accepted.Sensitivity != sensitivity || !accepted.Provenance.Equals(provenance)
            || (label is not null && label.ProducingMaintenanceReceiptDigest != paid.Attempt.DisclosureReceiptDigest)
            || paid.Attempt.Work with { Fence = current.Work.Fence } != current.Work)
        {
            throw new InvalidDataException();
        }

        await ValidateStoredWorkAssociationAsync(connection, transaction, paid.Attempt.Work, current.Work, cancellationToken).ConfigureAwait(false);

        ValidatePaidShape(paid, paid.Attempt.Work, LongRestProviderPayloadKind.Embedding,
            LongRestEmbeddingPayloadHash.Create(generation.Recipe.EmbeddingProvider, generation.Recipe.EmbeddingModel,
                generation.Recipe.EmbeddingDimensions, accepted.Summary).Value, generation.Recipe.EmbeddingDestinationDigest);

        ImmutableArray<LongRestTarget> targets = await ReadExpandedTargetsAsync(connection, transaction, generation, nodeId, capability, cancellationToken).ConfigureAwait(false);

        return new(new(nodeId, layer, group, membership, source, summary, label?.Sensitivity ?? ContentSensitivity.None,
            label?.Provenance ?? GenerationProvenance.CreateExact([]), label is null ? [] : [label.LabelDigest], targets), [.. vector]);
    }

    private static async Task<ImmutableArray<LongRestTarget>> ReadExpandedTargetsAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, Guid node, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        HierarchyLeafCursor cursor = new(node);

        ImmutableArray<LongRestTarget>.Builder targets = ImmutableArray.CreateBuilder<LongRestTarget>();

        while (targets.Count < LongRestDiscoveryLimits.ExpandedLeaves)
        {
            string? memory = await cursor.NextAsync(connection, transaction, generation.GenerationId, cancellationToken).ConfigureAwait(false);

            if (memory is null)
            {
                break;
            }

            targets.Add((await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false)).Target);
        }

        return targets.ToImmutable();
    }

    private static async Task BindCurrentWorkAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestWorkIdentity work, CancellationToken cancellationToken)
    {
        CovenantDigest digest = LongRestDiscoveryHash.Work(work).Value;

        await using SqliteCommand existing = Command(connection, transaction, """
            SELECT WorkItemId, SourceDigest, RecipeDigest FROM long_rest_work_items
            WHERE GenerationId = $generation AND KindCode = 2 AND ItemId = $item;
            """, ("$generation", Format(work.GenerationId)), ("$item", work.ItemId));

        string? unit = null;

        await using (SqliteDataReader reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (new CovenantDigest((byte[])reader[1]) != work.SourceDigest || new CovenantDigest((byte[])reader[2]) != work.RecipeDigest)
                {
                    throw new InvalidDataException();
                }

                unit = reader.GetString(0);
            }
        }

        if (unit is null)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId, SourceDigest, RecipeDigest, Revision, DispositionCode)
                VALUES ($unit, $work, $generation, $operation, 2, $item, $source, $recipe, 0, 1);
                """, cancellationToken, ("$unit", Format(Guid.NewGuid())), ("$work", digest.Bytes), ("$generation", Format(work.GenerationId)),
                ("$operation", work.OperationId.ToString("N")), ("$item", work.ItemId), ("$source", work.SourceDigest.Bytes), ("$recipe", work.RecipeDigest.Bytes)).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE long_rest_work_items SET WorkDigest = $work, Revision = Revision + 1
                WHERE WorkItemId = $unit AND WorkDigest <> $work AND DispositionCode = 1;
                """, cancellationToken, ("$work", digest.Bytes), ("$unit", unit)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-proves a generation's captured leaves against the live corpus. At a <see cref="LongRestVerificationDepth.Seam"/> that is every leaf,
    /// read in full; at a <see cref="LongRestVerificationDepth.Step"/> it is the set-based metadata question over every leaf and one rotating
    /// window of them read in full, which is what bounds how long a divergence no trigger saw can go unnoticed.
    /// </summary>
    internal static async Task VerifyFrozenSourcesAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestMaintenanceCapability? capability, LongRestVerificationDepth depth, CancellationToken cancellationToken)
    {
        if (!await FrozenSourceMetadataMatchesAsync(connection, transaction, generation, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException();
        }

        if (depth == LongRestVerificationDepth.Step)
        {
            if (!await OwnProjectionsOnlyAsync(connection, transaction, generation, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException();
            }

            await ScrubWindowAsync(connection, transaction, generation, capability, cancellationToken).ConfigureAwait(false);

            return;
        }

        LongRestVerificationCounters.CorpusPass();

        string? cursor = null;

        while (true)
        {
            List<string> memories = [];

            await using (SqliteCommand command = Command(connection, transaction,
                "SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation AND ($cursor IS NULL OR MemoryId > $cursor) ORDER BY MemoryId LIMIT 128;",
                ("$generation", Format(generation.GenerationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    memories.Add(reader.GetString(0));
                }
            }

            if (memories.Count == 0)
            {
                return;
            }

            foreach (string memory in memories)
            {
                _ = await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false);
            }

            cursor = memories[^1];
        }
    }

    private static Task<Result> ValidateFoldAuthorityAsync(LongRestWorkIdentity work, LongRestMaintenanceCapability? capability,
        CancellationToken cancellationToken) => work.Scope.ProtectedPartitionDigest is null ? Task.FromResult(Result.Success())
        : capability is null ? Task.FromResult(Result.Failure(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Hierarchy work requires current protected authority.")))
        : capability.ValidateWorkAsync(work, work.Fence.Configuration, cancellationToken).AsTask();

    private static async Task<LongRestDiscoveryGeneration> MoveHierarchyCursorAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, int layer, long group, LongRestSweepPhase phase, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE long_rest_discovery_generations SET FoldLayer = $layer, FoldGroup = $group, PhaseCode = $phase, Revision = Revision + 1
            WHERE GenerationId = $generation AND Revision = $revision AND PhaseCode = 2;
            """, ("$layer", layer), ("$group", group), ("$phase", (int)phase), ("$generation", Format(generation.GenerationId)), ("$revision", generation.Revision));

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return generation with { FoldLayer = layer, FoldGroup = group, Phase = phase, Revision = checked(generation.Revision + 1) };
    }
}
