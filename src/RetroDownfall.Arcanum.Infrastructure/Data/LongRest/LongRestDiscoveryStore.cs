using System.Data;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>Owns immutable source capture and generation coordinates on one admitted connection.</summary>
internal sealed partial class LongRestDiscoveryStore(ArcanumDbContext db, TimeProvider timeProvider) : ILongRestDiscoveryStore
{
    /// <summary>
    /// When set, replaces the two-minute default as the bound on how long clean readmission and completed-checkpoint reconciliation keep
    /// retrying SQLITE_BUSY/LOCKED - the only way a test can reach the busy timeout without waiting for it. Production never sets it.
    /// </summary>
    internal TimeSpan? BusyRetryDeadlineForTesting { get; set; }

    public async Task<Result<LongRestDiscoveryGeneration>> StartOrResumeAsync(Guid operationId, LongRestSweepScope scope,
        LongRestSweepFence fence, LongRestRecipeIdentity recipe, CancellationToken cancellationToken)
    {
        Result<CovenantDigest> recipeDigest = LongRestDiscoveryHash.Recipe(recipe);

        Guid generationId = Guid.NewGuid();

        if (recipeDigest.IsFailure || LongRestDiscoveryHash.Work(new(generationId, operationId, "capture",
                LongRestWorkKind.SourcePage, scope, fence, new CovenantDigest(new byte[32]),
                recipeDigest.IsSuccess ? recipeDigest.Value : default)).IsFailure)
        {
            return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.InvalidRequest, "Discovery requires an exact operation, scope, fence and recipe.");
        }

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                if (await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 18)
                {
                    return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.Unavailable, "Discovery storage is not ready.");
                }

                LongRestDiscoveryGeneration? existing = await ReadGenerationAsync(connection, transaction,
                    "OperationId = $id", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);

                if (existing is not null)
                {
                    if (existing.Scope != scope || existing.Recipe != recipe
                        || existing.Phase is LongRestSweepPhase.Completed or LongRestSweepPhase.Invalidated or LongRestSweepPhase.Retired)
                    {
                        return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The operation no longer owns active matching discovery facts.");
                    }

                    if (!await ValidateAndLinkSweepAsync(connection, transaction, operationId, existing.GenerationId, scope, cancellationToken).ConfigureAwait(false))
                    {
                        return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The sweep owns different scope or generation facts.");
                    }

                    // Resuming a retained generation is a seam. While it was parked, restored, evolved or erased around, it may have been changed by a
                    // route no trigger watches, so no unit runs until every leaf it froze has been re-proved. A divergence is a stale generation,
                    // which a clean sweep answers by being replaced, not a storage fault to retry.
                    if (!await VerifyResumeSeamAsync(connection, transaction, existing, cancellationToken).ConfigureAwait(false))
                    {
                        return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The retained generation no longer matches the sources it froze.");
                    }

                    if (existing.Fence == fence)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return Result<LongRestDiscoveryGeneration>.Success(existing);
                    }

                    if (!CanReadmit(existing.Fence, fence)
                        || !await FrozenSourceMetadataMatchesAsync(connection, transaction, existing, cancellationToken).ConfigureAwait(false))
                    {
                        return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "Only a new boot with unchanged frozen source facts can readmit this generation.");
                    }

                    await SettleInterruptedSendsAsync(connection, transaction, existing, cancellationToken).ConfigureAwait(false);

                    long nextRevision = checked(existing.Revision + 1);

                    await using SqliteCommand readmit = Command(connection, transaction, """
                        UPDATE long_rest_discovery_generations SET FenceJson = $fence, Revision = $next
                        WHERE GenerationId = $generation AND OperationId = $operation AND Revision = $expected AND PhaseCode NOT IN (6, 7, 9);
                        """, ("$fence", JsonSerializer.Serialize(LongRestStoredFence.From(fence), LongRestJsonContext.Default.LongRestStoredFence)),
                        ("$next", nextRevision), ("$generation", Format(existing.GenerationId)), ("$operation", operationId.ToString("N")),
                        ("$expected", existing.Revision));

                    if (await readmit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    {
                        return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The generation moved before boot re-admission.");
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return Result<LongRestDiscoveryGeneration>.Success(existing with { Fence = fence, Revision = nextRevision });
                }

                await using SqliteCommand command = Command(connection, transaction,
                    "SELECT count(*) FROM LongRunningOperations WHERE Id = $id AND Kind = 'long-rest-sweep';", ("$id", operationId.ToString("N")));

                if ((long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 1)
                {
                    return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.InvalidRequest, "The owning sweep operation does not exist.");
                }

                command.CommandText = "SELECT COALESCE(max(InsertionSequence), 0) FROM long_rest_sources;";

                long highwater = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

                if (fence.InsertionHighWatermark > highwater)
                {
                    return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The source high-watermark has not been observed by storage.");
                }

                await ExecuteAsync(connection, transaction, """
                    INSERT INTO long_rest_discovery_generations
                        (GenerationId, OperationId, ScopeKindCode, CampaignId, PartitionDigest, InsertionHighWatermark,
                         FenceJson, RecipeJson, RecipeDigest, PhaseCode, Revision, CreatedAtUtc)
                    VALUES ($generation, $operation, $scope, $campaign, $partition, $highwater,
                        $fence, $recipe, $digest, 1, 0, $at);
                    """, cancellationToken,
                    ("$generation", Format(generationId)), ("$operation", operationId.ToString("N")), ("$scope", (int)scope.Kind),
                    ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null), ("$partition", scope.ProtectedPartitionDigest?.Bytes),
                    ("$highwater", fence.InsertionHighWatermark),
                    ("$fence", JsonSerializer.Serialize(LongRestStoredFence.From(fence), LongRestJsonContext.Default.LongRestStoredFence)),
                    ("$recipe", JsonSerializer.Serialize(recipe, LongRestJsonContext.Default.LongRestRecipeIdentity)),
                    ("$digest", recipeDigest.Value.Bytes), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow()))).ConfigureAwait(false);

                if (!await ValidateAndLinkSweepAsync(connection, transaction, operationId, generationId, scope, cancellationToken).ConfigureAwait(false))
                {
                    return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The sweep owns different scope or generation facts.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Result<LongRestDiscoveryGeneration>.Success(new(generationId, operationId, scope, fence, recipe,
                    LongRestSweepPhase.Capture, 0, null, 0, 0, false));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.Unavailable, "Discovery storage could not preserve the exact generation.");
        }
    }

    private static async Task<bool> ValidateAndLinkSweepAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid operationId, Guid generationId, LongRestSweepScope scope, CancellationToken cancellationToken)
    {
        // BeginOrResume may precede discovery. Only the explicit operation with the same exact
        // scope and partition can acquire this generation, on initial start or later resumption.
        await using SqliteCommand link = Command(connection, transaction, """
            SELECT count(*) FROM long_rest_sweeps WHERE OperationId = $operation AND
                (ScopeKindCode <> $scope OR CampaignId IS NOT $campaign OR PartitionDigest IS NOT $partition
                 OR (GenerationId IS NOT NULL AND GenerationId <> $generation));
            """, ("$operation", operationId.ToString("N")), ("$scope", (int)scope.Kind),
            ("$campaign", scope.CampaignId is { } owner ? Format(owner) : null),
            ("$partition", scope.ProtectedPartitionDigest?.Bytes), ("$generation", Format(generationId)));

        if ((long)(await link.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! != 0)
        {
            return false;
        }

        await ExecuteAsync(connection, transaction,
            "UPDATE long_rest_sweeps SET GenerationId = $generation WHERE OperationId = $operation AND GenerationId IS NULL;",
            cancellationToken, ("$generation", Format(generationId)), ("$operation", operationId.ToString("N"))).ConfigureAwait(false);

        return true;
    }

    private static bool CanReadmit(LongRestSweepFence previous, LongRestSweepFence current) =>
        previous.Configuration.BootId != current.Configuration.BootId
        && previous.Configuration.ValueDigest == current.Configuration.ValueDigest
        && previous with
        {
            Configuration = current.Configuration,
            AdmissionGeneration = current.AdmissionGeneration,
            RuntimeAuthorityGeneration = current.RuntimeAuthorityGeneration,
            CapabilityGeneration = current.CapabilityGeneration,
        } == current;

    // No content or vector materialization and no accepted-stage association occurs here. Tracked epochs prove the retained observation
    // survived; current work revalidates its exact bytes and canonical labels under its fresh capability before executing or reusing paid
    // evidence. The label comparison is the loose, spelling-insensitive one on purpose: a mis-spelled protected label must still count.
    //
    // The last four questions are attribution, in both directions. A leaf's consolidated and transformation-output flags are projections of rows
    // the sweep itself writes when it applies a decision, so no trigger on them can tell a foreign writer from the sweep. What distinguishes them
    // is whose receipt set the flag: every receipt that sets one on a leaf of this generation must belong to one of its own committed work items,
    // unless the leaf was frozen with the flag already set. And a flag the leaf was frozen with must still be explained by a row: an ordinary
    // erasure of the memory a consolidation suppressed removes that consolidation's receipt, so the survivor stops being a transformation output
    // with nothing having written the survivor, and no trigger sees a row that only disappears. The questions are set-based and conservative (they
    // ask for attribution and for existence, not for the full proof of the receipt that the per-leaf read makes), so a step that skips the corpus
    // pass still refuses a foreign consolidation and a vanished one, which is exactly what the per-leaf read refuses.
    internal static readonly string FrozenSourceMetadataSql = $"""
        SELECT NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            LEFT JOIN long_rest_sources source ON source.MemoryId = leaf.MemoryId
            LEFT JOIN saga_memories memory ON memory.Id = leaf.MemoryId
            LEFT JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = leaf.MemoryId
            LEFT JOIN annal_heads head ON head.ClaimId = claim.ClaimId
            LEFT JOIN annal_versions version ON version.VersionId = head.CurrentVersionId
            LEFT JOIN saga_memory_embeddings embedding ON embedding.MemoryId = leaf.MemoryId
            WHERE leaf.GenerationId = $generation AND (
                source.MemoryId IS NULL OR memory.Id IS NULL OR head.CurrentVersionId IS NULL
                OR source.InsertionSequence <> leaf.InsertionSequence OR source.MutationEpoch <> leaf.MutationEpoch
                OR head.CurrentVersionId <> leaf.VersionId OR memory.ScopeKindCode <> $scope OR memory.CampaignId IS NOT $campaign
                OR NOT ({SourceOwnerEligible})
                OR embedding.Dim IS NOT leaf.EmbeddingDimensions
                OR ($partition IS NULL AND EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                    AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(leaf.MemoryId, '-', ''))))
                OR ($partition IS NOT NULL AND (SELECT count(*) FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                    AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(leaf.MemoryId, '-', ''))
                    AND label.SensitivityDigest = $partition) <> 1)))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            JOIN long_rest_suppressions suppression ON suppression.SourceVersionId = leaf.VersionId
            WHERE leaf.GenerationId = $generation AND COALESCE(json_extract(leaf.SnapshotJson, '$.isConsolidated'), 0) = 0
                AND suppression.ReceiptId NOT IN (SELECT ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND ReceiptId IS NOT NULL))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            JOIN long_rest_receipts receipt ON receipt.SurvivorVersionId = leaf.VersionId AND receipt.OutcomeCode = 1
            WHERE leaf.GenerationId = $generation AND COALESCE(json_extract(leaf.SnapshotJson, '$.isTransformationOutput'), 0) = 0
                AND receipt.ReceiptId NOT IN (SELECT ReceiptId FROM long_rest_work_items WHERE GenerationId = $generation AND ReceiptId IS NOT NULL))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            WHERE leaf.GenerationId = $generation AND json_extract(leaf.SnapshotJson, '$.isConsolidated') = 1
                AND NOT EXISTS (SELECT 1 FROM long_rest_suppressions suppression WHERE suppression.SourceVersionId = leaf.VersionId))
          AND NOT EXISTS (
            SELECT 1 FROM long_rest_discovery_leaves leaf
            WHERE leaf.GenerationId = $generation AND json_extract(leaf.SnapshotJson, '$.isTransformationOutput') = 1
                AND NOT EXISTS (SELECT 1 FROM long_rest_receipts receipt WHERE receipt.OutcomeCode = 1 AND receipt.SurvivorVersionId = leaf.VersionId));
        """;

    private static async Task<bool> FrozenSourceMetadataMatchesAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, CancellationToken cancellationToken)
    {
        LongRestVerificationCounters.AntiJoinPass();

        await using SqliteCommand command = Command(connection, transaction, FrozenSourceMetadataSql,
            ("$generation", Format(generation.GenerationId)), ("$scope", (int)generation.Scope.Kind),
            ("$campaign", generation.Scope.CampaignId is { } campaign ? Format(campaign) : null),
            ("$partition", generation.Scope.ProtectedPartitionDigest?.Bytes));

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task<LongRestDiscoveryGeneration?> ReadGenerationAsync(SqliteConnection connection,
        SqliteTransaction transaction, string predicate, string id, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT GenerationId, OperationId, ScopeKindCode, CampaignId, PartitionDigest, InsertionHighWatermark,
                FenceJson, RecipeJson, RecipeDigest, PhaseCode, Revision, CaptureCursor, FoldLayer, FoldGroup, Published
            FROM long_rest_discovery_generations WHERE {predicate};
            """, ("$id", id));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Result<LongRestSweepScope> parsedScope = LongRestSweepScope.Create(new((LongRestSweepScopeKind)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3))),
            reader.IsDBNull(4) ? null : new CovenantDigest((byte[])reader[4]));

        if (parsedScope.IsFailure)
        {
            throw new InvalidDataException();
        }

        LongRestSweepScope scope = parsedScope.Value;

        LongRestStoredFence stored = JsonSerializer.Deserialize(reader.GetString(6), LongRestJsonContext.Default.LongRestStoredFence)
            ?? throw new InvalidDataException();

        LongRestSweepFence fence = stored.ToDomain(scope);

        LongRestRecipeIdentity recipe = JsonSerializer.Deserialize(reader.GetString(7), LongRestJsonContext.Default.LongRestRecipeIdentity)
            ?? throw new InvalidDataException();

        Result<CovenantDigest> digest = LongRestDiscoveryHash.Recipe(recipe);

        if (digest.IsFailure || digest.Value != new CovenantDigest((byte[])reader[8]) || fence.InsertionHighWatermark != reader.GetInt64(5))
        {
            throw new InvalidDataException();
        }

        LongRestDiscoveryGeneration generation = new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), scope,
            fence, recipe, (LongRestSweepPhase)reader.GetInt32(9), reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt32(12), reader.GetInt64(13), reader.GetInt64(14) == 1);

        if (LongRestDiscoveryHash.Work(new(generation.GenerationId, generation.OperationId, "capture", LongRestWorkKind.SourcePage,
            scope, fence, new CovenantDigest(new byte[32]), digest.Value)).IsFailure)
        {
            throw new InvalidDataException();
        }

        return generation;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = Command(connection, transaction, sql, parameters);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Result<T> Refuse<T>(string code, string message) => new Error(code, message);

    private static bool StorageFailure(Exception exception) =>
        exception is SqliteException or InvalidDataException or FormatException or ArgumentException or JsonException or OverflowException;
}

/// <summary>Only serializable, content-free canonical owner facts; never paths or live authority.</summary>
internal sealed record LongRestStoredFence(long? CampaignAvailabilityGeneration, uint? PathIdentityPolicyVersion,
    long? PathIdentityRevision, CovenantDigest? RootIdentityDigest, long InsertionHighWatermark,
    Guid DatasetGeneration, long AdmissionGeneration, long RuntimeAuthorityGeneration,
    long CapabilityGeneration, LongRestConfigurationStamp Configuration)
{
    internal static LongRestStoredFence From(LongRestSweepFence value) => new(value.CanonicalScope.CampaignAvailabilityGeneration,
        value.CanonicalScope.PathIdentityPolicyVersion, value.CanonicalScope.PathIdentityRevision, value.CanonicalScope.RootIdentityDigest,
        value.InsertionHighWatermark, value.DatasetGeneration, value.AdmissionGeneration,
        value.RuntimeAuthorityGeneration, value.CapabilityGeneration, value.Configuration);

    internal LongRestSweepFence ToDomain(LongRestSweepScope scope) => new(CanonicalCampaignContext.Create(
        scope.CampaignId is { } campaign ? SessionCampaignBinding.ForCampaign(campaign) : SessionCampaignBinding.GlobalOnly,
        CampaignAvailabilityGeneration, PathIdentityPolicyVersion, PathIdentityRevision, RootIdentityDigest), InsertionHighWatermark,
        DatasetGeneration, AdmissionGeneration, RuntimeAuthorityGeneration, CapabilityGeneration, Configuration);
}
