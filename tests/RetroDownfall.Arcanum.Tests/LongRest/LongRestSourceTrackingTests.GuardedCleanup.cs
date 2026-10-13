using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSourceTrackingTests
{
    // Independent SHA256(domain UTF8 + presence byte 01 + exact summary UTF8), see fix report.
    private static readonly CovenantDigest ProtectedSummaryDigest = new(Convert.FromHexString(
        "16BF55E33CE9EB3D1048B6939F5F5B64C42E28A511754DEA25F2114462170701"));

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pin_of_protected_source_removes_labelled_hierarchy_without_live_label_delete_authority(bool foreignKeys)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        (string memory, Guid node) = await SeedProtectedProjectionAsync(h);

        await ExecuteAsync(connection, foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = 16 AND ArtifactId = $node;",
            ("$node", node.ToString("D").ToUpperInvariant())));

        Assert.Equal(19, refused.SqliteErrorCode);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_protected_fts WHERE long_rest_protected_fts MATCH 'tainttoken';"));

        SagaCurationOutcome pinned = await h.Store.SetPinAsync(memory, true, DateTimeOffset.Parse(At), CancellationToken.None);

        Assert.Equal(SagaCurationOutcomeKind.Applied, pinned.Kind);

        Assert.Equal(At, await ScalarAsync(connection, "SELECT PinnedAtUtc FROM saga_memories;"));

        await AssertProtectedProjectionRemovedAsync(connection);

        Assert.Equal(7L, await ScalarAsync(connection, "SELECT PhaseCode FROM long_rest_discovery_generations;"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 6;"));
    }

    [SkippableTheory]
    [InlineData("generation", true)]
    [InlineData("generation", false)]
    [InlineData("node", true)]
    [InlineData("node", false)]
    [InlineData("retire", true)]
    [InlineData("retire", false)]
    [InlineData("embedding", true)]
    [InlineData("embedding", false)]
    public async Task Protected_hierarchy_cleanup_is_owned_by_each_source_or_generation_mutation(string mutation, bool foreignKeys)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        (string memory, Guid node) = await SeedProtectedProjectionAsync(h);

        await ExecuteAsync(connection, foreignKeys ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;");

        if (mutation == "retire")
        {
            SagaCurationOutcome retired = await h.Store.RetireAsync(memory,
                RetroDownfall.Arcanum.Core.Annals.AnnalContentDigest.ForSagaMemory("source " + memory),
                DateTimeOffset.Parse(At), CancellationToken.None);

            Assert.Equal(SagaCurationOutcomeKind.Applied, retired.Kind);

            Assert.Equal(At, await ScalarAsync(connection, "SELECT RetiredAtUtc FROM saga_memories;"));
        }
        else
        {
            string sql = mutation switch
            {
                "generation" => "DELETE FROM long_rest_discovery_generations;",
                "node" => "DELETE FROM long_rest_discovery_nodes WHERE NodeId = $node;",
                _ => "UPDATE saga_memory_embeddings SET Embedding = zeroblob(256);",
            };

            await ExecuteAsync(connection, sql, ("$node", node.ToString("D").ToUpperInvariant()));
        }

        await AssertProtectedProjectionRemovedAsync(connection);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 6;"));

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = 6;"));

        Assert.Equal(19, refused.SqliteErrorCode);
    }

    [SkippableTheory]
    [InlineData("embedding")]
    [InlineData("parent-edge")]
    [InlineData("child-edge")]
    [InlineData("clean-fts")]
    [InlineData("protected-fts")]
    [InlineData("accepted-summary")]
    public async Task Orphan_kind_sixteen_label_cannot_be_deleted_while_a_projection_survives(string projection)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        _ = await SeedProtectedProjectionAsync(h);

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");

        Guid orphan = Guid.NewGuid();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            DerivedArtifactWrite write = new(SensitiveArtifactKind.LongRestDiscovery, orphan, null, null, null, 37,
                ProtectedSummaryDigest, ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()]));

            Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, write, CancellationToken.None)).IsSuccess);

            await transaction.CommitAsync();
        }

        // Deliberately retain a malformed FK-off companion, with a different GUID spelling.
        string sql = projection switch
        {
            "embedding" => """
                INSERT INTO long_rest_discovery_embeddings (NodeId, Provider, Model, Dimensions, ValueDigest, Vector, PaidEvidenceJson)
                VALUES ($orphan, 'provider', 'embedding', 64, zeroblob(32), zeroblob(256), '{}');
                """,
            "parent-edge" => "UPDATE long_rest_discovery_edges SET ParentNodeId = $orphan;",
            "child-edge" => "UPDATE long_rest_discovery_edges SET ChildNodeId = $orphan;",
            "clean-fts" => "INSERT INTO long_rest_clean_fts (NodeId, GenerationId, Content) SELECT $orphan, GenerationId, 'tainttoken' FROM long_rest_discovery_generations;",
            "protected-fts" => "INSERT INTO long_rest_protected_fts (NodeId, GenerationId, Content) SELECT $orphan, GenerationId, 'tainttoken' FROM long_rest_discovery_generations;",
            _ => """
                INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId,
                    SourceDigest, RecipeDigest, Revision, DispositionCode)
                    SELECT 'orphan-work', randomblob(32), GenerationId, OperationId, 1, $orphan,
                        SourceDigest, RecipeDigest, 0, 1 FROM long_rest_work_items WHERE WorkItemId = 'work';
                INSERT INTO long_rest_accepted_summaries (WorkItemId, WorkDigest, GenerationId, NodeId, MembershipDigest,
                    SourceDigest, RecipeDigest, PayloadDigest, Summary, SummaryDigest, EvidenceJson, CreatedAtUtc)
                    SELECT 'orphan-work', WorkDigest, GenerationId, $orphan, MembershipDigest, SourceDigest, RecipeDigest,
                        PayloadDigest, Summary, SummaryDigest, EvidenceJson, CreatedAtUtc FROM long_rest_accepted_summaries WHERE WorkItemId = 'work';
                """,
        };

        await ExecuteAsync(connection, sql, ("$orphan", orphan.ToString("N")));

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection,
            "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = 16 AND ArtifactId = $orphan;",
            ("$orphan", orphan.ToString("D").ToUpperInvariant())));

        Assert.Equal(19, refused.SqliteErrorCode);

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));
    }

    [SkippableFact]
    public async Task Protected_pin_cleanup_preserves_an_unrelated_campaign_generation_and_label()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        (string memory, Guid node) = await SeedProtectedProjectionAsync(h);

        Guid otherNode = Guid.NewGuid();

        Guid otherCampaign = Guid.NewGuid();

        await ExecuteAsync(connection, """
            INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary)
                VALUES ('other-operation', 'long-rest-sweep', 1, 1, $at, 'other');
            INSERT INTO long_rest_discovery_generations (GenerationId, OperationId, ScopeKindCode, CampaignId,
                PartitionDigest, InsertionHighWatermark, FenceJson, RecipeJson, RecipeDigest, PhaseCode, Revision, CreatedAtUtc)
                SELECT 'other-generation', 'other-operation', 2, $campaign, PartitionDigest, 0, '{}', '{}',
                    RecipeDigest, 2, 0, $at FROM long_rest_discovery_generations;
            INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MembershipDigest,
                SourceDigest, RecipeDigest, Summary, SummaryDigest, SensitivityCode, ProvenanceJson, PartitionDigest, Revision)
                SELECT $other, 'other-generation', Layer, GroupOrdinal, MembershipDigest, SourceDigest, RecipeDigest,
                    Summary, SummaryDigest, SensitivityCode, ProvenanceJson, PartitionDigest, Revision
                FROM long_rest_discovery_nodes WHERE NodeId = $node;
            INSERT INTO long_rest_discovery_embeddings (NodeId, Provider, Model, Dimensions, ValueDigest, Vector, PaidEvidenceJson)
                SELECT $other, Provider, Model, Dimensions, ValueDigest, Vector, PaidEvidenceJson FROM long_rest_discovery_embeddings;
            INSERT INTO long_rest_protected_fts (NodeId, GenerationId, Content) VALUES ($other, 'other-generation', 'tainttoken summary');
            """, ("$node", node.ToString("D").ToUpperInvariant()), ("$other", otherNode.ToString("D").ToUpperInvariant()),
            ("$campaign", otherCampaign.ToString("D").ToUpperInvariant()), ("$at", At));

        ArtifactSensitivityLabel otherLabel;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            ArtifactSensitivityLabel original = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
                SensitiveArtifactKind.LongRestDiscovery, node, CancellationToken.None)).Value!;

            DerivedArtifactWrite write = new(SensitiveArtifactKind.LongRestDiscovery, otherNode, null, otherCampaign, null,
                original.ArtifactRevision, original.ArtifactContentDigest, original.Sensitivity, original.Provenance);

            Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, write, CancellationToken.None)).IsSuccess);

            otherLabel = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
                SensitiveArtifactKind.LongRestDiscovery, otherNode, CancellationToken.None)).Value!;

            Assert.True((await LongRestLifecycle.ValidateArtifactAsync(connection, transaction, otherLabel, CancellationToken.None)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.Equal(SagaCurationOutcomeKind.Applied,
            (await h.Store.SetPinAsync(memory, true, DateTimeOffset.Parse(At), CancellationToken.None)).Kind);

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_discovery_nodes WHERE GenerationId = 'other-generation';"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_protected_fts WHERE long_rest_protected_fts MATCH 'tainttoken';"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT PhaseCode FROM long_rest_discovery_generations WHERE GenerationId = 'other-generation';"));

        await using SqliteTransaction verification = connection.BeginTransaction();

        ArtifactSensitivityLabel retained = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, verification,
            SensitiveArtifactKind.LongRestDiscovery, otherNode, CancellationToken.None)).Value!;

        Assert.Equal(otherLabel.LabelId, retained.LabelId);

        Assert.Equal(otherLabel.LabelDigest, retained.LabelDigest);

        Assert.True((await LongRestLifecycle.ValidateArtifactAsync(connection, verification, retained, CancellationToken.None)).IsSuccess);
    }

    [SkippableFact]
    public async Task Protected_pin_rolls_back_when_a_dangling_projection_blocks_orphan_label_cleanup()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        (string memory, Guid node) = await SeedProtectedProjectionAsync(h);

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;");

        await ExecuteAsync(connection, """
            INSERT INTO long_rest_protected_fts (NodeId, GenerationId, Content)
            VALUES ($node, 'dangling-generation', 'tainttoken dangling');
            """, ("$node", node.ToString("N")));

        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(() =>
            h.Store.SetPinAsync(memory, true, DateTimeOffset.Parse(At), CancellationToken.None));

        Assert.Equal(19, refused.SqliteErrorCode);

        Assert.Equal(DBNull.Value, await ScalarAsync(connection, "SELECT PinnedAtUtc FROM saga_memories;"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_discovery_nodes;"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT PhaseCode FROM long_rest_discovery_generations;"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_protected_fts WHERE long_rest_protected_fts MATCH 'tainttoken';"));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    private static async Task<(string Memory, Guid Node)> SeedProtectedProjectionAsync(SagaStoreHarness h)
    {
        Guid memory = Guid.NewGuid();

        string memoryId = memory.ToString("D");

        await SeedSourceAsync(h, memoryId);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        ArtifactSensitivityLabel sourceLabel;

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            DerivedArtifactWrite write = new(SensitiveArtifactKind.Saga, memory, null, null, null, 37,
                DerivedArtifactContentDigest.ForText("source " + memoryId), ContentSensitivity.CovenantDerived, provenance);

            Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, write, CancellationToken.None)).IsSuccess);

            sourceLabel = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
                SensitiveArtifactKind.Saga, memory, CancellationToken.None)).Value!;

            await transaction.CommitAsync();
        }

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        Guid operation = Guid.NewGuid();

        await ExecuteAsync(connection, "INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, $at, 'sweep');",
            ("$id", operation.ToString("N")), ("$at", At));

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(
            CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        LongRestConfigurationStamp configuration = new(Guid.NewGuid(), 1, new CovenantDigest(new byte[32]));

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null), sourceLabel.SensitivityDigest).Value;

        LongRestSweepFence fence = new(CanonicalCampaignContext.GlobalOnly, 1, authority.Snapshot.DatasetGeneration!.Value,
            1, authority.Snapshot.RuntimeAuthorityGeneration, authority.Snapshot.CapabilityGeneration, configuration);

        LongRestRecipeIdentity recipe = new(1, 1, "provider", "summary", configuration.ValueDigest, "provider", "embedding", configuration.ValueDigest, 64);

        LongRestDiscoveryGeneration generation = (await store.StartOrResumeAsync(operation, scope, fence, recipe, CancellationToken.None)).Value;

        LongRestMaintenanceCapability capability = await CapabilityAsync(operation, configuration, authority);

        Assert.True((await capability.ConsumeAsync(operation, scope, configuration, CancellationToken.None)).IsSuccess);

        Result<LongRestSourcePage> captured = await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, capability, CancellationToken.None);

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.ToString() : "");

        Assert.Single(captured.Value.Leaves);

        Guid node = Guid.NewGuid();

        await ExecuteAsync(connection, """
            INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MembershipDigest,
                SourceDigest, RecipeDigest, Summary, SummaryDigest, SensitivityCode, ProvenanceJson, PartitionDigest, Revision)
            VALUES ($node, $generation, 1, 1, zeroblob(32), zeroblob(32), zeroblob(32), 'tainttoken summary',
                $digest, 1, $provenance, $partition, 37);
            INSERT INTO long_rest_discovery_edges (GenerationId, ParentNodeId, Ordinal, ChildNodeId)
                SELECT $generation, $node, 1, NodeId FROM long_rest_discovery_nodes WHERE Layer = 0;
            INSERT INTO long_rest_discovery_embeddings (NodeId, Provider, Model, Dimensions, ValueDigest, Vector, PaidEvidenceJson)
                VALUES ($node, 'provider', 'embedding', 64, zeroblob(32), zeroblob(256), '{}');
            INSERT INTO long_rest_protected_fts (NodeId, GenerationId, Content) VALUES ($node, $generation, 'tainttoken summary');
            INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId,
                SourceDigest, RecipeDigest, Revision, DispositionCode)
                VALUES ('work', zeroblob(32), $generation, $operation, 1, $node, zeroblob(32), zeroblob(32), 0, 1);
            INSERT INTO long_rest_work_inputs (WorkItemId, Ordinal, MemoryId, VersionId, SourceEvidenceJson)
                SELECT 'work', 1, MemoryId, VersionId, SnapshotJson FROM long_rest_discovery_leaves;
            INSERT INTO long_rest_accepted_summaries (WorkItemId, WorkDigest, GenerationId, NodeId, MembershipDigest,
                SourceDigest, RecipeDigest, PayloadDigest, Summary, SummaryDigest, EvidenceJson, CreatedAtUtc)
                VALUES ('work', zeroblob(32), $generation, $node, zeroblob(32), zeroblob(32), zeroblob(32),
                    zeroblob(32), 'tainttoken summary', $digest, '{}', $at);
            INSERT INTO long_rest_adjudications (WorkItemId, WorkDigest, DecisionKindCode, PayloadDigest, EvidenceJson, CreatedAtUtc)
                VALUES ('work', zeroblob(32), 1, zeroblob(32), '{}', $at);
            INSERT INTO long_rest_provider_effects (WorkItemId, WorkDigest, OriginalWorkJson, AttemptId, PayloadKindCode, PhysicalOrdinal,
                Revision, StateCode, PayloadDigest, DestinationDigest, UpdatedAtUtc)
                VALUES ('work', zeroblob(32), '{}', 'attempt', 1, 1, 0, 1, zeroblob(32), zeroblob(32), $at);
            """, ("$node", node.ToString("D").ToUpperInvariant()), ("$generation", generation.GenerationId.ToString("D").ToUpperInvariant()),
            ("$operation", operation.ToString("N")), ("$digest", ProtectedSummaryDigest.Bytes),
            ("$provenance", JsonSerializer.Serialize(provenance, LongRestJsonContext.Default.GenerationProvenance)),
            ("$partition", sourceLabel.SensitivityDigest.Bytes), ("$at", At));

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            DerivedArtifactWrite write = new(SensitiveArtifactKind.LongRestDiscovery, node, null, null, null, 37,
                ProtectedSummaryDigest, ContentSensitivity.CovenantDerived, provenance);

            Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction, write, CancellationToken.None)).IsSuccess);

            ArtifactSensitivityLabel label = (await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
                SensitiveArtifactKind.LongRestDiscovery, node, CancellationToken.None)).Value!;

            Assert.True((await LongRestLifecycle.ValidateArtifactAsync(connection, transaction, label, CancellationToken.None)).IsSuccess);

            await transaction.CommitAsync();
        }

        return (memoryId, node);
    }

    private static async Task AssertProtectedProjectionRemovedAsync(SqliteConnection connection)
    {
        string[] tables = ["long_rest_discovery_nodes", "long_rest_discovery_leaves", "long_rest_discovery_embeddings",
            "long_rest_discovery_edges", "long_rest_work_items", "long_rest_work_inputs", "long_rest_provider_effects",
            "long_rest_accepted_summaries", "long_rest_adjudications", "long_rest_clean_fts", "long_rest_protected_fts"];

        foreach (string table in tables)
        {
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM {table};"));
        }

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_protected_fts WHERE long_rest_protected_fts MATCH 'tainttoken';"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 16;"));
    }
}
