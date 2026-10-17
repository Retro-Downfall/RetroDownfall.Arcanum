using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    // The immutable binding, never Sessions.CampaignId, supplies ownership. Canonical identity
    // spelling is enforced by the persisted binding schema; the reader also uses the shared classifier.
    private const string OwnerBinding = """
        ((memory.SessionId IS NULL AND memory.ScopeKindCode = 1 AND memory.CampaignId IS NULL)
            OR (memory.SessionId IS NOT NULL AND EXISTS (
                SELECT 1 FROM Sessions owner JOIN session_campaign_bindings binding ON binding.SessionId = owner.Id
                WHERE owner.Id = memory.SessionId COLLATE NOCASE AND binding.BindingKindCode = memory.ScopeKindCode
                    AND binding.BindingKindCode IN (1, 2) AND binding.CampaignId IS memory.CampaignId)))
        """;

    // The version the memory is currently at proves the owner by naming the same Session. This is the exact match, and the only one a protected
    // partition accepts: its label names the Session and the revision it proves.
    private const string SourceOwnerEligible = $"""
        memory.SessionId IS version.SourceSessionId COLLATE NOCASE
        AND {OwnerBinding}
        """;

    // The same, for a statement that also carries PartitionEligibility and binds $partition. The one version that cannot name a Session is the
    // operator's own (OriginCode 1): a correction or a reinstatement is stated by no Session, so the Annals write it with no source Session while
    // the memory keeps its owner. In the clean partition (no sensitivity label, which PartitionEligibility has already required) that memory stays
    // a source, its owner proved by its row and the Session's immutable binding, so an operator's correction does not take a Session's memory out
    // of every sweep. A protected partition, a system-backfilled version and an agent's version that names no Session keep the exact match.
    private const string CleanAwareSourceOwnerEligible = $"""
        (memory.SessionId IS version.SourceSessionId COLLATE NOCASE
            OR ($partition IS NULL AND version.OriginCode = 1 AND version.SourceSessionId IS NULL AND memory.SessionId IS NOT NULL))
        AND {OwnerBinding}
        """;

    private const string PartitionEligibility = """
        (($partition IS NULL AND COALESCE(version.SensitivityCode, 0) = 0 AND NOT EXISTS (
            SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', ''))))
            OR ($partition IS NOT NULL AND EXISTS (
            SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', ''))
                AND label.SensitivityDigest = $partition)))
        """;

    // A source takes part in a sweep only while it is neither pinned, nor retired, nor suppressed by a consolidation of its current version.
    // None of those can be an anchor or a neighbour, so none is captured: it is not a leaf, is never folded or summarized, and its text is never
    // sent to a provider. The protected partition scan and the completed-corpus proof read this same definition, so the corpus a sweep captures,
    // the partitions that exist and the debt a completed sweep must have covered are the same sources.
    private const string SourceLifecycleEligible = """
        memory.PinnedAtUtc IS NULL AND memory.RetiredAtUtc IS NULL
        AND NOT EXISTS (SELECT 1 FROM long_rest_suppressions WHERE SourceVersionId = head.CurrentVersionId)
        """;

    // Scope, lifecycle and partition eligibility precede pagination. No source text or vector is selected here.
    internal static readonly string EligibleSources = $"""
        FROM long_rest_sources source
        JOIN saga_memories memory ON memory.Id = source.MemoryId
        LEFT JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
        LEFT JOIN annal_heads head ON head.ClaimId = claim.ClaimId
        LEFT JOIN annal_versions version ON version.VersionId = head.CurrentVersionId
        WHERE source.InsertionSequence <= $highwater AND ($cursor IS NULL OR source.MemoryId > $cursor)
            AND memory.ScopeKindCode = $scope AND memory.CampaignId IS $campaign
            AND {SourceLifecycleEligible}
            AND ({CleanAwareSourceOwnerEligible})
            AND ({PartitionEligibility})
        """;

    internal static readonly string CapturePageSql = "SELECT source.MemoryId, source.InsertionSequence, source.MutationEpoch, head.CurrentVersionId "
        + EligibleSources + " ORDER BY source.MemoryId LIMIT $limit;";

    public async Task<Result<LongRestSourcePage>> CaptureNextSourcePageAsync(Guid generationId, long expectedRevision,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        if (generationId == Guid.Empty || expectedRevision < 0)
        {
            return Refuse<LongRestSourcePage>(ErrorCodes.LongRest.InvalidRequest, "Capture requires an exact generation revision.");
        }

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                LongRestVerificationCounters.Transaction(deferred: false);

                LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction,
                    "GenerationId = $id", Format(generationId), cancellationToken).ConfigureAwait(false);

                if (generation is null || generation.Revision != expectedRevision || generation.Phase != LongRestSweepPhase.Capture)
                {
                    return Refuse<LongRestSourcePage>(ErrorCodes.LongRest.StaleInput, "The capture generation or expected revision is no longer current.");
                }

                Result authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

                if (authority.IsFailure)
                {
                    return Result<LongRestSourcePage>.Failure(authority.Error);
                }

                List<SourceCoordinate> coordinates = [];

                await using (SqliteCommand command = SourceCommand(connection, transaction, generation, generation.CaptureCursor, CapturePageSql))
                {
                    _ = command.Parameters.AddWithValue("$limit", LongRestDiscoveryLimits.SourcePage);

                    await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (reader.IsDBNull(3))
                        {
                            return Refuse<LongRestSourcePage>(ErrorCodes.LongRest.MissingClaim, "A source has no retained current claim; capture cannot advance past it.");
                        }

                        coordinates.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
                    }
                }

                ImmutableArray<LongRestSourceLeaf>.Builder leaves = ImmutableArray.CreateBuilder<LongRestSourceLeaf>(coordinates.Count);

                foreach (SourceCoordinate coordinate in coordinates)
                {
                    Result<LongRestSourceLeaf> loaded = await ReadSourceAsync(connection, transaction, generation, coordinate,
                        capability, materializeContent: true, cancellationToken).ConfigureAwait(false);

                    if (loaded.IsFailure)
                    {
                        return Result<LongRestSourcePage>.Failure(loaded.Error);
                    }

                    leaves.Add(loaded.Value);
                }

                ImmutableArray<LongRestSourceLeaf> page = leaves.MoveToImmutable();

                Result<CovenantDigest> pageDigest = LongRestDiscoveryHash.SourcePage(generation.Scope, page);

                if (pageDigest.IsFailure)
                {
                    return Result<LongRestSourcePage>.Failure(pageDigest.Error);
                }

                string? cursor = coordinates.Count == 0 ? generation.CaptureCursor : coordinates[^1].MemoryId;

                bool hasMore;

                await using (SqliteCommand command = SourceCommand(connection, transaction, generation, cursor,
                    "SELECT EXISTS (SELECT 1 " + EligibleSources + " LIMIT 1);"))
                {
                    hasMore = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
                }

                foreach (LongRestSourceLeaf leaf in page)
                {
                    await WriteLeafAsync(connection, transaction, generation, leaf, cancellationToken).ConfigureAwait(false);
                }

                authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

                if (authority.IsFailure)
                {
                    return Result<LongRestSourcePage>.Failure(authority.Error);
                }

                long revision = checked(expectedRevision + 1);

                await using SqliteCommand advance = Command(connection, transaction, """
                    UPDATE long_rest_discovery_generations SET CaptureCursor = $cursor, Revision = $next, PhaseCode = $phase
                    WHERE GenerationId = $generation AND Revision = $expected AND PhaseCode = 1;
                    """, ("$cursor", cursor), ("$next", revision), ("$phase", hasMore ? 1 : 2),
                    ("$generation", Format(generationId)), ("$expected", expectedRevision));

                if (await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    return Refuse<LongRestSourcePage>(ErrorCodes.LongRest.StaleInput, "The capture cursor moved before publication.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Result<LongRestSourcePage>.Success(new(generationId, revision, page, pageDigest.Value, cursor, hasMore));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestSourcePage>(ErrorCodes.LongRest.Unavailable, "The source page could not be captured with verified bindings.");
        }
    }

    private static async Task<Result<LongRestSourceLeaf>> ReadSourceAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, SourceCoordinate coordinate, LongRestMaintenanceCapability? capability,
        bool materializeContent, CancellationToken cancellationToken)
    {
        // Measure before loading so a corrupt or oversized source never becomes an unbounded allocation.
        await using SqliteCommand metadata = Command(connection, transaction, """
            SELECT length(CAST(memory.Content AS BLOB)), embedding.Dim, length(embedding.Embedding), memory.SessionId,
                version.SourceSessionId, memory.ScopeKindCode, memory.CampaignId, owner.Id, binding.BindingKindCode, binding.CampaignId,
                version.OriginCode,
                EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                    AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', '')))
            FROM saga_memories memory
            LEFT JOIN saga_memory_embeddings embedding ON embedding.MemoryId = memory.Id
            LEFT JOIN annal_versions version ON version.VersionId = $version
            LEFT JOIN Sessions owner ON owner.Id = memory.SessionId COLLATE NOCASE
            LEFT JOIN session_campaign_bindings binding ON binding.SessionId = owner.Id
            WHERE memory.Id = $memory;
            """, ("$memory", coordinate.MemoryId), ("$version", coordinate.VersionId));

        bool hasEmbedding;

        int dimensions = 0;

        Guid? session;

        await using (SqliteDataReader reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetInt64(0) > LongRestDiscoveryLimits.ProviderInputUtf8Bytes)
            {
                return Refuse<LongRestSourceLeaf>(ErrorCodes.LongRest.Unavailable, "A source exceeds the bounded capture surface.");
            }

            session = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));

            Guid? retainedSession = reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4));

            (SagaMemoryScopeKind kind, string? ownerCampaign) = SagaMemoryScopeClassifier.Classify(session is not null,
                reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9));

            string? expectedCampaign = generation.Scope.CampaignId is { } campaign ? Format(campaign) : null;

            // The operator's own version of a memory with no label names no Session (see CleanAwareSourceOwnerEligible); every other version names its owner.
            bool operatorVersion = generation.Scope.ProtectedPartitionDigest is null && session is not null && retainedSession is null
                && !reader.IsDBNull(10) && reader.GetInt32(10) == (int)AnnalOrigin.OperatorStated && reader.GetInt64(11) == 0;

            if ((session != retainedSession && !operatorVersion) || (session is not null && reader.IsDBNull(7))
                || (int)kind != (int)generation.Scope.Kind || ownerCampaign != expectedCampaign
                || reader.GetInt32(5) != (int)kind || (reader.IsDBNull(6) ? null : reader.GetString(6)) != ownerCampaign)
            {
                return Refuse<LongRestSourceLeaf>(ErrorCodes.LongRest.StaleInput, "The source no longer has its exact retained Session and authoritative scope.");
            }

            hasEmbedding = !reader.IsDBNull(1);

            if (hasEmbedding)
            {
                dimensions = reader.GetInt32(1);

                if (dimensions is < 1 or > 4096 || reader.IsDBNull(2) || reader.GetInt64(2) != dimensions * sizeof(float))
                {
                    return Refuse<LongRestSourceLeaf>(ErrorCodes.LongRest.Unavailable, "A source embedding has an invalid stored dimension or length.");
                }
            }
        }

        Result<SagaLongRestService.LoadedSnapshot> loaded = await SagaLongRestService.ReadSnapshotAsync(connection, transaction,
            new(coordinate.MemoryId, coordinate.VersionId, new string('0', 64)), cancellationToken, materializeContent).ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            return Result<LongRestSourceLeaf>.Failure(loaded.Error);
        }

        LongRestSnapshot snapshot = loaded.Value.Snapshot;

        string? content = loaded.Value.Content;

        ArtifactSensitivityLabel? label = null;

        CovenantDigest? sagaContentDigest = null;

        if (generation.Scope.ProtectedPartitionDigest is { } partition)
        {
            if (!Guid.TryParseExact(coordinate.MemoryId, "D", out Guid artifact)
                || coordinate.MemoryId != artifact.ToString("D"))
            {
                return Refuse<LongRestSourceLeaf>(ErrorCodes.Covenant.IntegrityFailure, "The protected source identity is not canonical.");
            }

            Result<ArtifactSensitivityLabel?> labeled = await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection,
                transaction, SensitiveArtifactKind.Saga, artifact, cancellationToken).ConfigureAwait(false);

            if (labeled.IsFailure)
            {
                return Result<LongRestSourceLeaf>.Failure(labeled.Error);
            }

            label = labeled.Value;

            if (label is null || label.Sensitivity != ContentSensitivity.CovenantDerived || label.Provenance.IsEmpty
                || label.SensitivityDigest != partition || label.SessionId != session
                || label.SessionId != snapshot.SourceSessionId || label.CampaignId != snapshot.CampaignId)
            {
                return Refuse<LongRestSourceLeaf>(ErrorCodes.Covenant.IntegrityFailure, "The canonical label does not cover this source owner and partition.");
            }

            await using (SqliteCommand proof = Command(connection, transaction, """
                SELECT count(*) = 1 AND min(ArtifactLabelDigest = $label AND SensitivityDigest = $partition)
                FROM artifact_sensitivity WHERE ArtifactKindCode = 6
                    AND lower(replace(ArtifactId, '-', '')) = lower(replace($memory, '-', ''));
                """, ("$memory", coordinate.MemoryId), ("$label", label.LabelDigest.Bytes), ("$partition", partition.Bytes)))
            {
                if ((long)(await proof.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 1)
                {
                    return Refuse<LongRestSourceLeaf>(ErrorCodes.Covenant.IntegrityFailure, "A source has an ambiguous or unverified sensitivity manifest.");
                }
            }

            Result live = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

            if (live.IsFailure)
            {
                return Result<LongRestSourceLeaf>.Failure(live.Error);
            }

            // The canonical label supplies the observed Saga revision. Annals revision and source
            // MutationEpoch remain independent fences; neither can invent a second Saga revision.
            await using SqliteCommand plaintext = Command(connection, transaction,
                "SELECT Content FROM saga_memories WHERE Id = $memory;", ("$memory", coordinate.MemoryId));

            string text = (string)(await plaintext.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            sagaContentDigest = DerivedArtifactContentDigest.ForText(text);

            if (sagaContentDigest != label.ArtifactContentDigest
                || !CryptographicOperations.FixedTimeEquals(AnnalContentDigest.ForSagaMemory(text), Convert.FromHexString(snapshot.ContentHash)))
            {
                return Refuse<LongRestSourceLeaf>(ErrorCodes.Covenant.IntegrityFailure, "Source text no longer matches its canonical label and Annals content bindings.");
            }

            content = materializeContent ? text : null;
        }
        else if (snapshot.HasSensitivityLabel || snapshot.Sensitivity != ContentSensitivity.None)
        {
            return Refuse<LongRestSourceLeaf>(ErrorCodes.Covenant.ForbiddenAuthority, "Protected source material cannot enter a clean generation.");
        }

        if (content is not null && !CryptographicOperations.FixedTimeEquals(AnnalContentDigest.ForSagaMemory(content), Convert.FromHexString(snapshot.ContentHash)))
        {
            return Refuse<LongRestSourceLeaf>(ErrorCodes.LongRest.StaleInput, "The current source text no longer matches its Annals version.");
        }

        LongRestSourceEmbeddingBinding? embedding = null;

        ImmutableArray<float> vector = [];

        if (hasEmbedding)
        {
            await using SqliteCommand command = Command(connection, transaction,
                "SELECT Embedding FROM saga_memory_embeddings WHERE MemoryId = $memory;", ("$memory", coordinate.MemoryId));

            byte[] bytes = (byte[])(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            float[] values = EmbeddingBlobCodec.Decode(bytes);

            if (values.Any(static value => !float.IsFinite(value)))
            {
                return Refuse<LongRestSourceLeaf>(ErrorCodes.LongRest.Unavailable, "A source embedding contains nonfinite values.");
            }

            // Even metadata-only reads bind the authoritative raw bytes, never current provider configuration.
            embedding = new(LongRestSourceEmbeddingKind.ObservedSaga, dimensions, new CovenantDigest(SHA256.HashData(bytes)));

            if (materializeContent)
            {
                vector = [.. values];
            }
        }

        return new LongRestSourceLeaf(snapshot, coordinate.Sequence, coordinate.Epoch, embedding,
            label?.Sensitivity ?? ContentSensitivity.None, label?.Provenance ?? GenerationProvenance.CreateExact([]),
            label is null ? [] : [label.LabelDigest], content, vector, label, sagaContentDigest, label?.ArtifactRevision);
    }

    private static async Task WriteLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestSourceLeaf leaf, CancellationToken cancellationToken)
    {
        CovenantDigest sourceDigest = LongRestDiscoveryHash.SourcePage(generation.Scope, [leaf]).Value;

        await ExecuteAsync(connection, transaction, """
            INSERT INTO long_rest_discovery_leaves (GenerationId, MemoryId, InsertionSequence, MutationEpoch, VersionId,
                SnapshotJson, SourceDigest, EmbeddingDigest, EmbeddingDimensions, LabelJson)
            VALUES ($generation, $memory, $sequence, $epoch, $version, $snapshot, $source, $embedding, $dimensions, $label);
            """, cancellationToken, ("$generation", Format(generation.GenerationId)), ("$memory", leaf.Snapshot.MemoryId),
            ("$sequence", leaf.InsertionSequence), ("$epoch", leaf.MutationEpoch), ("$version", leaf.Snapshot.VersionId),
            ("$snapshot", JsonSerializer.Serialize(leaf.Snapshot, LongRestJsonContext.Default.LongRestSnapshot)),
            ("$source", sourceDigest.Bytes), ("$embedding", leaf.EmbeddingBinding?.ValueDigest.Bytes),
            ("$dimensions", leaf.EmbeddingBinding?.Dimensions),
            ("$label", leaf.CanonicalLabel is null ? null : JsonSerializer.Serialize(leaf.CanonicalLabel, LongRestJsonContext.Default.ArtifactSensitivityLabel))).ConfigureAwait(false);

        Guid node = Guid.NewGuid();

        await ExecuteAsync(connection, transaction, """
            INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MemoryId, MembershipDigest,
                SourceDigest, RecipeDigest, Summary, SummaryDigest, SensitivityCode, ProvenanceJson, PartitionDigest, Revision)
            VALUES ($node, $generation, 0, $group, $memory, $source, $source, $recipe, NULL, NULL, $sensitivity, $provenance, $partition, 0);
            """, cancellationToken, ("$node", Format(node)), ("$generation", Format(generation.GenerationId)),
            ("$group", leaf.InsertionSequence), ("$memory", leaf.Snapshot.MemoryId), ("$source", sourceDigest.Bytes),
            ("$recipe", LongRestDiscoveryHash.Recipe(generation.Recipe).Value.Bytes), ("$sensitivity", (int)leaf.Sensitivity),
            ("$provenance", leaf.CanonicalLabel is null ? null : JsonSerializer.Serialize(leaf.Provenance, LongRestJsonContext.Default.GenerationProvenance)),
            ("$partition", generation.Scope.ProtectedPartitionDigest?.Bytes)).ConfigureAwait(false);

        string index = leaf.CanonicalLabel is null ? "long_rest_clean_fts" : "long_rest_protected_fts";

        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {index} (NodeId, GenerationId, Content) VALUES ($node, $generation, $content);",
            cancellationToken, ("$node", Format(node)), ("$generation", Format(generation.GenerationId)), ("$content", leaf.Content)).ConfigureAwait(false);
    }

    private static async Task<Result> ValidateReadAuthorityAsync(LongRestDiscoveryGeneration generation,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        if (generation.Scope.ProtectedPartitionDigest is null)
        {
            return Result.Success();
        }

        if (capability is null)
        {
            return Result.Failure(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Protected capture requires matching live request authority."));
        }

        return await capability.ValidateWorkAsync(new(generation.GenerationId, generation.OperationId, "capture", LongRestWorkKind.SourcePage,
            generation.Scope, generation.Fence, new CovenantDigest(new byte[32]), LongRestDiscoveryHash.Recipe(generation.Recipe).Value),
            generation.Fence.Configuration, cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand SourceCommand(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string? cursor, string sql) => Command(connection, transaction, sql,
        ("$highwater", generation.Fence.InsertionHighWatermark), ("$cursor", cursor), ("$scope", (int)generation.Scope.Kind),
        ("$campaign", generation.Scope.CampaignId is { } campaign ? Format(campaign) : null),
        ("$partition", generation.Scope.ProtectedPartitionDigest?.Bytes));

    private sealed record SourceCoordinate(string MemoryId, long Sequence, long Epoch, string VersionId);
}
