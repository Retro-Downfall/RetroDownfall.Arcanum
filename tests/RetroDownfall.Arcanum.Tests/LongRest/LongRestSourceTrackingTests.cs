using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSourceTrackingTests
{
    private const string At = "2026-10-10T00:00:00.0000000Z";

    [SkippableFact]
    public async Task New_source_above_highwater_is_future_debt()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedSourceAsync(h, "z-existing");

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration generation = await StartAsync(h, store);

        await SeedSourceAsync(h, "a-new");

        Result<LongRestSourcePage> captured = await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, null, CancellationToken.None);

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.ToString() : "");

        Assert.Equal(["z-existing"], captured.Value.Leaves.Select(leaf => leaf.Snapshot.MemoryId));

        Assert.False(captured.Value.HasMore);

        await ExecuteAsync((SqliteConnection)h.Connection, "UPDATE long_rest_discovery_generations SET PhaseCode = 6;");

        LongRestDiscoveryGeneration next = await StartAsync(h, store);

        Result<LongRestSourcePage> debt = await store.CaptureNextSourcePageAsync(next.GenerationId, 0, null, CancellationToken.None);

        Assert.True(debt.IsSuccess, debt.IsFailure ? debt.Error.ToString() : "");

        Assert.Equal(["a-new", "z-existing"], debt.Value.Leaves.Select(leaf => leaf.Snapshot.MemoryId));
    }

    [SkippableFact]
    public async Task Random_identity_behind_cursor_is_not_skipped()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        for (int i = 0; i < 130; i++)
        {
            await SeedSourceAsync(h, $"m-{i:D3}");
        }

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration generation = await StartAsync(h, store);

        Result<LongRestSourcePage> first = await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, null, CancellationToken.None);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.ToString() : "");

        Assert.Equal(128, first.Value.Leaves.Length);

        Assert.True(first.Value.HasMore);

        await SeedSourceAsync(h, "a-random-behind");

        Result<LongRestSourcePage> second = await store.CaptureNextSourcePageAsync(generation.GenerationId, first.Value.Revision, null, CancellationToken.None);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.ToString() : "");

        Assert.Equal(["m-128", "m-129"], second.Value.Leaves.Select(leaf => leaf.Snapshot.MemoryId));

        await ExecuteAsync((SqliteConnection)h.Connection, "UPDATE long_rest_discovery_generations SET PhaseCode = 6;");

        LongRestDiscoveryGeneration next = await StartAsync(h, store);

        Result<LongRestSourcePage> debt = await store.CaptureNextSourcePageAsync(next.GenerationId, 0, null, CancellationToken.None);

        Assert.True(debt.IsSuccess, debt.IsFailure ? debt.Error.ToString() : "");

        Assert.Equal("a-random-behind", debt.Value.Leaves[0].Snapshot.MemoryId);
    }

    [SkippableFact]
    public async Task Capture_binds_actual_source_vector_bytes_and_rejects_stale_revision()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedSourceAsync(h, "source");

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration generation = await StartAsync(h, store);

        Result<LongRestSourcePage> captured = await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, null, CancellationToken.None);

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.ToString() : "");

        LongRestSourceLeaf leaf = Assert.Single(captured.Value.Leaves);

        Assert.Equal(LongRestSourceEmbeddingKind.ObservedSaga, leaf.EmbeddingBinding!.Kind);

        byte[] bytes = (byte[])(await ScalarAsync((SqliteConnection)h.Connection, "SELECT Embedding FROM saga_memory_embeddings;"))!;

        Assert.Equal(new CovenantDigest(System.Security.Cryptography.SHA256.HashData(bytes)), leaf.EmbeddingBinding.ValueDigest);

        Assert.Equal(64, leaf.Vector.Length);

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, null, CancellationToken.None)).IsFailure);

        Assert.Equal(1L, await ScalarAsync((SqliteConnection)h.Connection, "SELECT count(*) FROM long_rest_discovery_leaves;"));
    }

    private static Task SeedSourceAsync(SagaStoreHarness h, string id) =>
        h.Store.InsertAsync(id, "source " + id, new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero),
            null, null, "extraction", h.Embedding(), CancellationToken.None);

    private static async Task<LongRestDiscoveryGeneration> StartAsync(SagaStoreHarness h, LongRestDiscoveryStore store)
    {
        Guid operation = Guid.NewGuid();

        await ExecuteAsync((SqliteConnection)h.Connection,
            "INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, $at, 'sweep');",
            ("$id", operation.ToString("N")), ("$at", At));

        long highwater = (long)(await ScalarAsync((SqliteConnection)h.Connection, "SELECT COALESCE(max(InsertionSequence), 0) FROM long_rest_sources;"))!;

        CovenantDigest digest = new(new byte[32]);

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        LongRestSweepFence fence = new(CanonicalCampaignContext.GlobalOnly, highwater, Guid.NewGuid(), 1, 1, 1, new(Guid.NewGuid(), 0, digest));

        LongRestRecipeIdentity recipe = new(1, 1, "provider", "summary", digest, "provider", "embedding", digest, 64);

        Result<LongRestDiscoveryGeneration> result = await store.StartOrResumeAsync(operation, scope, fence, recipe, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");

        Result<LongRestDiscoveryGeneration> resumed = await store.StartOrResumeAsync(operation, scope, fence, recipe, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.ToString() : "");

        Assert.Equal(result.Value, resumed.Value);

        return result.Value;
    }

    [Fact]
    public async Task Delete_reinsert_has_a_new_sequence()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        long before = (long)(await ScalarAsync(scratch.Connection, "SELECT InsertionSequence FROM long_rest_sources WHERE MemoryId = 'subject';"))!;

        await ExecuteAsync(scratch.Connection, "DELETE FROM saga_memories WHERE Id = 'subject';");

        await MemoryAsync(scratch.Connection, "subject");

        long after = (long)(await ScalarAsync(scratch.Connection, "SELECT InsertionSequence FROM long_rest_sources WHERE MemoryId = 'subject';"))!;

        Assert.True(after > before);
    }

    [Theory]
    [InlineData("Content = 'revised'")]
    [InlineData("PinnedAtUtc = '2026-10-10T00:00:00.0000000Z'")]
    [InlineData("RetiredAtUtc = '2026-10-10T00:00:00.0000000Z'")]
    [InlineData("ScopeKindCode = 3")]
    public async Task Source_changes_advance_epoch_without_changing_sequence(string assignment)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        long sequence = (long)(await ScalarAsync(scratch.Connection, "SELECT InsertionSequence FROM long_rest_sources;"))!;

        long epoch = (long)(await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"))!;

        await ExecuteAsync(scratch.Connection, $"UPDATE saga_memories SET {assignment} WHERE Id = 'subject';");

        Assert.Equal(sequence, await ScalarAsync(scratch.Connection, "SELECT InsertionSequence FROM long_rest_sources;"));

        Assert.Equal(epoch + 1, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"));
    }

    [Fact]
    public async Task Embedding_replacement_advances_the_exact_source_epoch()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await ExecuteAsync(scratch.Connection, "INSERT INTO saga_memory_embeddings VALUES ('subject', zeroblob(256), 64);");

        long before = (long)(await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"))!;

        await ExecuteAsync(scratch.Connection, "UPDATE saga_memory_embeddings SET Embedding = randomblob(256) WHERE MemoryId = 'subject';");

        Assert.Equal(before + 1, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Protection_purges_clean_tokens_and_dependent_summaries_in_the_label_transaction(bool foreignKeys)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA");

        await SeedProjectionAsync(scratch.Connection, "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA");

        Assert.Equal(2L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_clean_fts WHERE long_rest_clean_fts MATCH 'tainttoken';"));

        if (!foreignKeys)
        {
            await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF;");
        }

        await ExecuteAsync(scratch.Connection,
            "BEGIN; INSERT INTO artifact_sensitivity (LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode, ExactGenerationIds, ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc) VALUES ('label', 6, 'aaaaaaaa111141118111aaaaaaaaaaaa', 1, 1, zeroblob(16), 0, zeroblob(32), zeroblob(32), zeroblob(32), $at);",
            ("$at", At));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_clean_fts WHERE long_rest_clean_fts MATCH 'tainttoken';"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_discovery_nodes;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_discovery_edges;"));

        Assert.Equal(7L, await ScalarAsync(scratch.Connection, "SELECT PhaseCode FROM long_rest_discovery_generations;"));

        await ExecuteAsync(scratch.Connection, "COMMIT;");

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT v FROM long_rest_clean_fts_config WHERE k = 'secure-delete';"));
    }

    [Fact]
    public async Task Foreign_key_disabled_deletion_removes_all_discovery_companions()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await SeedProjectionAsync(scratch.Connection, "subject");

        await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF; DELETE FROM saga_memories WHERE Id = 'subject';");

        foreach (string table in new[] { "long_rest_sources", "long_rest_discovery_leaves", "long_rest_discovery_nodes", "long_rest_discovery_embeddings", "long_rest_discovery_edges", "long_rest_clean_fts", "long_rest_protected_fts" })
        {
            Assert.Equal(0L, await ScalarAsync(scratch.Connection, $"SELECT count(*) FROM {table};"));
        }
    }

    [Fact]
    public async Task Mutation_of_included_source_invalidates_generation()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await SeedProjectionAsync(scratch.Connection, "subject");

        await ExecuteAsync(scratch.Connection, "UPDATE saga_memories SET Content = 'changed' WHERE Id = 'subject';");

        Assert.Equal(7L, await ScalarAsync(scratch.Connection, "SELECT PhaseCode FROM long_rest_discovery_generations;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_clean_fts;"));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("operation")]
    public async Task Foreign_key_disabled_owner_deletion_purges_generation_companions(string owner)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await SeedProjectionAsync(scratch.Connection, "subject");

        await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF;");

        await ExecuteAsync(scratch.Connection, owner == "generation"
            ? "DELETE FROM long_rest_discovery_generations WHERE GenerationId = 'generation';"
            : "DELETE FROM LongRunningOperations WHERE Id = 'operation';");

        foreach (string table in new[] { "long_rest_discovery_generations", "long_rest_discovery_leaves", "long_rest_discovery_nodes", "long_rest_discovery_edges", "long_rest_discovery_embeddings", "long_rest_clean_fts", "long_rest_protected_fts" })
        {
            Assert.Equal(0L, await ScalarAsync(scratch.Connection, $"SELECT count(*) FROM {table};"));
        }

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM saga_memories;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Kind_sixteen_purge_owns_nodes_vectors_edges_and_search_tokens(bool foreignKeys)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await SeedProjectionAsync(scratch.Connection, "subject");

        const string artifact = "AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA";

        await ExecuteAsync(scratch.Connection, "DELETE FROM long_rest_discovery_edges; DELETE FROM long_rest_discovery_embeddings; UPDATE long_rest_discovery_nodes SET NodeId = $artifact WHERE NodeId = 'parent'; INSERT INTO long_rest_discovery_embeddings VALUES ($artifact, 'provider', 'model', 64, zeroblob(32), zeroblob(256), '{}'); UPDATE long_rest_clean_fts SET NodeId = $artifact WHERE NodeId = 'parent'; INSERT INTO long_rest_discovery_edges VALUES ('generation', $artifact, 1, 'leaf');", ("$artifact", artifact));

        if (!foreignKeys)
        {
            await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF;");
        }

        await using SqliteTransaction transaction = scratch.Connection.BeginTransaction();

        CovenantArtifactPlanTally measured = await CovenantArtifactPlanRunner.RunAsync(scratch.Connection, transaction,
            SensitiveArtifactKind.LongRestDiscovery, CovenantIdentitySql.Key(Guid.Parse(artifact)), CovenantArtifactPlanMode.Count, CancellationToken.None);

        Assert.Equal(2L, measured.ArtifactRows);

        _ = await CovenantArtifactPlanRunner.RunAsync(scratch.Connection, transaction,
            SensitiveArtifactKind.LongRestDiscovery, CovenantIdentitySql.Key(Guid.Parse(artifact)), CovenantArtifactPlanMode.Delete, CancellationToken.None);

        await transaction.CommitAsync();

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_discovery_edges;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_clean_fts WHERE long_rest_clean_fts MATCH 'summary';"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Current_work_can_adopt_a_new_boot_without_rewriting_accepted_paid_evidence(bool foreignKeys)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await SeedProjectionAsync(scratch.Connection, "subject");

        await ExecuteAsync(scratch.Connection, """
            INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId,
                SourceDigest, RecipeDigest, Revision, DispositionCode)
            VALUES ('item', zeroblob(32), 'generation', 'operation', 2, 'fold-1', zeroblob(32), zeroblob(32), 0, 1);
            INSERT INTO long_rest_accepted_summaries (WorkItemId, WorkDigest, GenerationId, NodeId, MembershipDigest,
                SourceDigest, RecipeDigest, PayloadDigest, Summary, SummaryDigest, EvidenceJson, CreatedAtUtc)
            VALUES ('item', zeroblob(32), 'generation', 'accepted', zeroblob(32), zeroblob(32), zeroblob(32),
                zeroblob(32), 'accepted exact summary', zeroblob(32), '{"boot":"A","paid":true}', $at);
            """, ("$at", At));

        string before = (string)(await ScalarAsync(scratch.Connection, "SELECT hex(WorkDigest) || Summary || EvidenceJson FROM long_rest_accepted_summaries;"))!;

        await ExecuteAsync(scratch.Connection, "UPDATE long_rest_work_items SET WorkDigest = randomblob(32), Revision = 1 WHERE WorkItemId = 'item';");

        Assert.Equal(before, await ScalarAsync(scratch.Connection, "SELECT hex(WorkDigest) || Summary || EvidenceJson FROM long_rest_accepted_summaries;"));

        Assert.Equal(1L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_work_items item JOIN long_rest_accepted_summaries accepted USING (WorkItemId) WHERE item.WorkDigest <> accepted.WorkDigest;"));

        if (!foreignKeys)
        {
            await ExecuteAsync(scratch.Connection, "PRAGMA foreign_keys = OFF;");
        }

        await ExecuteAsync(scratch.Connection, "DELETE FROM LongRunningOperations WHERE Id = 'operation';");

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_work_items;"));

        Assert.Equal(0L, await ScalarAsync(scratch.Connection, "SELECT count(*) FROM long_rest_accepted_summaries;"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("text")]
    [InlineData("revision")]
    [InlineData("owner")]
    [InlineData("partition")]
    public async Task Kind_sixteen_revalidates_actual_summary_content_and_canonical_label_bindings(string defect)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        await SeedProjectionAsync(scratch.Connection, "subject");

        Guid node = Guid.NewGuid();

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        CovenantDigest digest = ProtectedSummaryDigest;

        ArtifactSensitivityLabel label = new(Guid.NewGuid(), SensitiveArtifactKind.LongRestDiscovery, node,
            null, defect == "owner" ? Guid.NewGuid() : null, null, 37, digest, ContentSensitivity.CovenantDerived,
            provenance, null, null, null, DateTimeOffset.UtcNow);

        await ExecuteAsync(scratch.Connection, """
            DELETE FROM long_rest_discovery_edges;
            DELETE FROM long_rest_discovery_embeddings;
            UPDATE long_rest_discovery_nodes SET NodeId = $node, Revision = $revision, Summary = $text,
                SummaryDigest = $digest, SensitivityCode = 1, PartitionDigest = $partition, ProvenanceJson = $provenance
            WHERE NodeId = 'parent';
            """, ("$node", node.ToString("D").ToUpperInvariant()), ("$revision", defect == "revision" ? 38 : 37),
            ("$text", defect == "text" ? "changed summary" : "tainttoken summary"), ("$digest", digest.Bytes),
            ("$partition", defect == "partition" ? new byte[32] : label.SensitivityDigest.Bytes),
            ("$provenance", JsonSerializer.Serialize(provenance, LongRestJsonContext.Default.GenerationProvenance)));

        await using SqliteTransaction transaction = scratch.Connection.BeginTransaction();

        Result validated = await LongRestLifecycle.ValidateArtifactAsync(scratch.Connection, transaction, label, CancellationToken.None);

        Assert.Equal(defect == "valid", validated.IsSuccess);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("nonfinite-vector")]
    public async Task Unverifiable_sources_never_advance_capture(string defect)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedSourceAsync(h, "source");

        SqliteConnection connection = (SqliteConnection)h.Connection;

        if (defect == "oversized")
        {
            await ExecuteAsync(connection, "UPDATE saga_memories SET Content = $content;", ("$content", new string('a', 32769)));
        }
        else
        {
            float[] vector = h.Embedding();

            vector[0] = float.NaN;

            await ExecuteAsync(connection, "UPDATE saga_memory_embeddings SET Embedding = $vector;", ("$vector", EmbeddingBlobCodec.Encode(vector)));
        }

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration generation = await StartAsync(h, store);

        Assert.True((await store.CaptureNextSourcePageAsync(generation.GenerationId, 0, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT Revision FROM long_rest_discovery_generations;"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_discovery_leaves;"));
    }

    [Fact]
    public async Task Attachment_provenance_and_delete_reinsert_with_replace_change_source_identity()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "subject");

        long sequence = (long)(await ScalarAsync(scratch.Connection, "SELECT InsertionSequence FROM long_rest_sources;"))!;

        long epoch = (long)(await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"))!;

        await ExecuteAsync(scratch.Connection, "INSERT INTO saga_memory_attachment_provenance VALUES ('subject', 'session', 'AAAAAAAA-1111-4111-8111-AAAAAAAAAAAA', 'key', 1, 'hash', $at, 'text');", ("$at", At));

        Assert.Equal(epoch + 1, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"));

        await ExecuteAsync(scratch.Connection, "UPDATE saga_memory_attachment_provenance SET ContentHash = 'changed';");

        Assert.Equal(epoch + 2, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"));

        await ExecuteAsync(scratch.Connection, "DELETE FROM saga_memory_attachment_provenance;");

        Assert.Equal(epoch + 3, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources;"));

        await ExecuteAsync(scratch.Connection, "PRAGMA recursive_triggers = OFF; INSERT OR REPLACE INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode) VALUES ('subject', 'replacement', $at, 1);", ("$at", At));

        Assert.True((long)(await ScalarAsync(scratch.Connection, "SELECT InsertionSequence FROM long_rest_sources;"))! > sequence);
    }

    [SkippableTheory]
    [InlineData("new-boot", true)]
    [InlineData("same-boot-epoch", false)]
    [InlineData("config-value", false)]
    [InlineData("highwater", false)]
    [InlineData("dataset", false)]
    [InlineData("same-boot-admission", false)]
    [InlineData("completed", false)]
    [InlineData("mutated", false)]
    public async Task Only_a_genuine_new_boot_can_readmit_unchanged_frozen_metadata(string change, bool allowed)
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedSourceAsync(h, "source");

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration original = await StartAsync(h, store);

        Result<LongRestSourcePage> page = await store.CaptureNextSourcePageAsync(original.GenerationId, 0, null, CancellationToken.None);

        Assert.True(page.IsSuccess);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        string frozen = (string)(await ScalarAsync(connection, "SELECT SnapshotJson || hex(SourceDigest) FROM long_rest_discovery_leaves;"))!;

        CovenantDigest paidWork = LongRestDiscoveryHash.Work(new(original.GenerationId, original.OperationId, "fold-1",
            LongRestWorkKind.HierarchyFold, original.Scope, original.Fence, page.Value.SourcePageDigest,
            LongRestDiscoveryHash.Recipe(original.Recipe).Value)).Value;

        await ExecuteAsync(connection, """
            INSERT INTO long_rest_work_items (WorkItemId, WorkDigest, GenerationId, OperationId, KindCode, ItemId,
                SourceDigest, RecipeDigest, Revision, DispositionCode)
            VALUES ('item', $work, $generation, $operation, 2, 'fold-1', $source, $recipe, 0, 1);
            INSERT INTO long_rest_accepted_summaries (WorkItemId, WorkDigest, GenerationId, NodeId, MembershipDigest,
                SourceDigest, RecipeDigest, PayloadDigest, Summary, SummaryDigest, EvidenceJson, CreatedAtUtc)
            VALUES ('item', $work, $generation, 'accepted', zeroblob(32), $source, $recipe,
                zeroblob(32), 'accepted summary', zeroblob(32), $evidence, $at);
            """, ("$work", paidWork.Bytes), ("$generation", original.GenerationId.ToString("D").ToUpperInvariant()),
            ("$operation", original.OperationId.ToString("N")), ("$source", page.Value.SourcePageDigest.Bytes),
            ("$recipe", LongRestDiscoveryHash.Recipe(original.Recipe).Value.Bytes),
            ("$evidence", "{\"boot\":\"" + original.Fence.Configuration.BootId.ToString("D") + "\"}"), ("$at", At));

        string paidBefore = (string)(await ScalarAsync(connection, "SELECT hex(WorkDigest) || EvidenceJson || Summary FROM long_rest_accepted_summaries;"))!;

        LongRestSweepFence next = original.Fence with
        {
            Configuration = original.Fence.Configuration with { BootId = Guid.NewGuid(), Epoch = 17 },
            AdmissionGeneration = original.Fence.AdmissionGeneration + 1,
            RuntimeAuthorityGeneration = original.Fence.RuntimeAuthorityGeneration + 1,
            CapabilityGeneration = original.Fence.CapabilityGeneration + 1,
        };

        next = change switch
        {
            "same-boot-epoch" => original.Fence with { Configuration = original.Fence.Configuration with { Epoch = 17 } },
            "config-value" => next with { Configuration = next.Configuration with { ValueDigest = new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()) } },
            "highwater" => next with { InsertionHighWatermark = original.Fence.InsertionHighWatermark + 1 },
            "dataset" => next with { DatasetGeneration = Guid.NewGuid() },
            "same-boot-admission" => original.Fence with { AdmissionGeneration = original.Fence.AdmissionGeneration + 1 },
            _ => next,
        };

        if (change == "completed")
        {
            await ExecuteAsync(connection, "UPDATE long_rest_discovery_generations SET PhaseCode = 6;");
        }
        else if (change == "mutated")
        {
            await ExecuteAsync(connection, "UPDATE saga_memories SET Content = 'changed';");
        }

        Result<LongRestDiscoveryGeneration> resumed = await store.StartOrResumeAsync(original.OperationId, original.Scope, next, original.Recipe, CancellationToken.None);

        Assert.Equal(allowed, resumed.IsSuccess);

        if (allowed)
        {
            Assert.Equal(original.GenerationId, resumed.Value.GenerationId);

            Assert.Equal(LongRestSweepPhase.Hierarchy, resumed.Value.Phase);

            Assert.Equal(page.Value.CommittedCaptureCursor, resumed.Value.CaptureCursor);

            Assert.Equal(page.Value.Revision + 1, resumed.Value.Revision);

            Assert.Equal(next, resumed.Value.Fence);

            Assert.Equal(frozen, await ScalarAsync(connection, "SELECT SnapshotJson || hex(SourceDigest) FROM long_rest_discovery_leaves;"));

            Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_provider_effects;"));

            CovenantDigest currentWork = LongRestDiscoveryHash.Work(new(original.GenerationId, original.OperationId, "fold-1",
                LongRestWorkKind.HierarchyFold, original.Scope, resumed.Value.Fence, page.Value.SourcePageDigest,
                LongRestDiscoveryHash.Recipe(original.Recipe).Value)).Value;

            Assert.NotEqual(paidWork, currentWork);

            await ExecuteAsync(connection, "UPDATE long_rest_work_items SET WorkDigest = $work WHERE WorkItemId = 'item';", ("$work", currentWork.Bytes));

            Assert.Equal(paidBefore, await ScalarAsync(connection, "SELECT hex(WorkDigest) || EvidenceJson || Summary FROM long_rest_accepted_summaries;"));
        }
    }

    [SkippableFact]
    public async Task Resumption_cannot_borrow_a_sweep_with_different_partition_ownership()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        await SeedSourceAsync(h, "source");

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration generation = await StartAsync(h, store);

        await ExecuteAsync((SqliteConnection)h.Connection, """
            INSERT INTO long_rest_sweeps (OperationId, GenerationId, ScopeKindCode, PartitionDigest,
                ConfigurationJson, PhaseCode, Revision, UpdatedAtUtc)
            VALUES ($operation, $generation, 1, zeroblob(32), '{}', 1, 0, $at);
            """, ("$operation", generation.OperationId.ToString("N")),
            ("$generation", generation.GenerationId.ToString("D").ToUpperInvariant()), ("$at", At));

        Assert.True((await store.StartOrResumeAsync(generation.OperationId, generation.Scope,
            generation.Fence, generation.Recipe, CancellationToken.None)).IsFailure);
    }

    [SkippableFact]
    public async Task An_active_global_clean_generation_cannot_be_claimed_by_another_operation()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        LongRestDiscoveryStore store = new(h.Context, TimeProvider.System);

        LongRestDiscoveryGeneration generation = await StartAsync(h, store);

        Guid other = Guid.NewGuid();

        await ExecuteAsync((SqliteConnection)h.Connection,
            "INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, $at, 'sweep');",
            ("$id", other.ToString("D").ToUpperInvariant()), ("$at", At));

        Assert.True((await store.StartOrResumeAsync(other, generation.Scope, generation.Fence, generation.Recipe, CancellationToken.None)).IsFailure);

        Assert.Equal(1L, await ScalarAsync((SqliteConnection)h.Connection, "SELECT count(*) FROM long_rest_discovery_generations;"));
    }

    private static async Task SeedProjectionAsync(SqliteConnection connection, string memory)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ('operation', 'long-rest-sweep', 1, 1, $at, 'sweep');
            INSERT INTO long_rest_discovery_generations (GenerationId, OperationId, ScopeKindCode, CampaignId, PartitionDigest, InsertionHighWatermark, FenceJson, RecipeJson, RecipeDigest, PhaseCode, Revision, CreatedAtUtc) VALUES ('generation', 'operation', 1, NULL, NULL, 1, '{}', '{}', zeroblob(32), 1, 0, $at);
            INSERT INTO long_rest_discovery_leaves (GenerationId, MemoryId, InsertionSequence, MutationEpoch, VersionId, SnapshotJson, SourceDigest, EmbeddingDigest, EmbeddingDimensions, LabelJson) SELECT 'generation', MemoryId, InsertionSequence, MutationEpoch, 'v1', '{}', zeroblob(32), NULL, NULL, NULL FROM long_rest_sources WHERE MemoryId = $memory;
            INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MemoryId, MembershipDigest, SourceDigest, RecipeDigest, Summary, SummaryDigest, SensitivityCode, Revision) VALUES ('leaf', 'generation', 0, 1, $memory, zeroblob(32), zeroblob(32), zeroblob(32), NULL, NULL, 0, 0);
            INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MemoryId, MembershipDigest, SourceDigest, RecipeDigest, Summary, SummaryDigest, SensitivityCode, Revision) VALUES ('parent', 'generation', 1, 1, NULL, zeroblob(32), zeroblob(32), zeroblob(32), 'tainttoken summary', zeroblob(32), 0, 0);
            INSERT INTO long_rest_discovery_edges (GenerationId, ParentNodeId, Ordinal, ChildNodeId) VALUES ('generation', 'parent', 1, 'leaf');
            INSERT INTO long_rest_discovery_embeddings (NodeId, Provider, Model, Dimensions, ValueDigest, Vector, PaidEvidenceJson) VALUES ('parent', 'provider', 'model', 64, zeroblob(32), zeroblob(256), '{}');
            INSERT INTO long_rest_clean_fts (NodeId, GenerationId, Content) VALUES ('leaf', 'generation', 'tainttoken source');
            INSERT INTO long_rest_clean_fts (NodeId, GenerationId, Content) VALUES ('parent', 'generation', 'tainttoken summary');
            """, ("$memory", memory), ("$at", At));
    }

    private static Task MemoryAsync(SqliteConnection connection, string id) => ExecuteAsync(connection,
        "INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode) VALUES ($id, 'tainttoken source', $at, 1);", ("$id", id), ("$at", At));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in values)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync();
    }

    private sealed class Scratch(EvolutionScratchDatabase file, SqliteConnection connection) : IAsyncDisposable
    {
        internal SqliteConnection Connection => connection;

        internal static async Task<Scratch> StartAsync()
        {
            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 64, CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'long_rest_sources';"));

            return new Scratch(file, connection);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();

            file.Dispose();
        }
    }
}
