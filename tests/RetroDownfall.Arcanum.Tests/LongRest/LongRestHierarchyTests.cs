using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed partial class LongRestHierarchyTests
{
    [SkippableFact]
    public async Task Read_fold_returns_exact_sources_without_advancing_the_paid_cursor()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("a", "amber observation");

        await h.SeedAsync("b", "ochre observation");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestHierarchyWork? next = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None));

        Assert.NotNull(next);

        Assert.Equal(["a", "b"], next.Fold.Children.SelectMany(child => child.ExpandedTargets).Select(target => target.MemoryId));

        Assert.All(next.Fold.Children, child => Assert.Equal(0, child.Layer));

        Assert.True(LongRestProviderPayload.Summary(next.Fold).IsSuccess);

        Assert.Equal(0L, await h.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));

        LongRestHierarchyWork? again = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None));

        Assert.Equal(LongRestDiscoveryHash.Work(next.Fold.Work).Value, LongRestDiscoveryHash.Work(again!.Fold.Work).Value);

        Assert.Equal(next.Fold.Children.Select(child => child.NodeId), again.Fold.Children.Select(child => child.NodeId));
    }

    [SkippableFact]
    public async Task A_singleton_publishes_its_existing_leaf_without_a_provider_effect()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("only", "sole observation");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        Assert.Null(LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)));

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes;"));

        long revision = (long)(await h.ScalarAsync("SELECT Revision FROM long_rest_discovery_generations;"))!;

        LongRestDiscoveryGeneration published = LongRestSweepHarness.Require(await h.Store.PublishAsync(generation.GenerationId, revision, generation.Fence, null, CancellationToken.None));

        Assert.True(published.Published);

        Assert.Equal(LongRestSweepPhase.Discover, published.Phase);
    }

    [SkippableFact]
    public async Task Edges_accept_carried_lower_layers_and_refuse_cycles_and_shared_children()
    {
        await using SagaStoreHarness h = await SagaStoreHarness.CreateAsync(annalsEnabled: true);

        SqliteConnection connection = (SqliteConnection)h.Connection;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MembershipDigest, SourceDigest,
                RecipeDigest, Summary, SummaryDigest, SensitivityCode, Revision)
            VALUES ('one', 'g', 1, 1, zeroblob(32), zeroblob(32), zeroblob(32), 'one', zeroblob(32), 0, 0),
                   ('two', 'g', 2, 2, zeroblob(32), zeroblob(32), zeroblob(32), 'two', zeroblob(32), 0, 0),
                   ('three', 'g', 3, 3, zeroblob(32), zeroblob(32), zeroblob(32), 'three', zeroblob(32), 0, 0),
                   ('four', 'g', 4, 4, zeroblob(32), zeroblob(32), zeroblob(32), 'four', zeroblob(32), 0, 0),
                   ('foreign', 'other', 1, 1, zeroblob(32), zeroblob(32), zeroblob(32), 'foreign', zeroblob(32), 0, 0);
            INSERT INTO long_rest_discovery_edges VALUES ('g', 'three', 1, 'one');
            INSERT INTO long_rest_discovery_edges VALUES ('g', 'three', 2, 'two');
            """;

        Assert.Null(await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync()));

        foreach ((string parent, string child) in new[] { ("one", "three"), ("three", "three"), ("three", "four"), ("three", "foreign"), ("four", "one"), ("three", "one") })
        {
            command.CommandText = "INSERT INTO long_rest_discovery_edges VALUES ('g', $parent, 3, $child);";

            command.Parameters.Clear();

            command.Parameters.AddWithValue("$parent", parent);

            command.Parameters.AddWithValue("$child", child);

            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        }
    }

    [SkippableFact]
    public async Task Persisted_vectors_preserve_membership_candidates_groups_and_receipts_across_enumeration()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int index = 0; index < 128; index++)
        {
            string memory = $"m-{index:D3}";

            await h.SeedAsync(memory, index == 0 || index >= 112 ? $"opal observation {index:D3}" : $"distinct observation {index:D3}");

            float[] vector = new float[64];

            vector[0] = index < 112 ? 1 : 0;

            vector[1] = index >= 112 ? 1 : index >= 96 ? 0.125f : 0;

            await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Embedding = $vector WHERE MemoryId = $memory;",
                ("$vector", EmbeddingBlobCodec.Encode(vector)), ("$memory", memory));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        CovenantDigest recipe = LongRestDiscoveryHash.Recipe(generation.Recipe).Value;

        // Both enumerations read the same captured corpus in one transaction. Only SQL ordering
        // changes: no recapture, rewritten node identity, substitute source hash or paid stage.
        await using SqliteTransaction transaction = h.Connection.BeginTransaction();

        var forward = await ReadPageAsync("ASC");

        var reverse = await ReadPageAsync("DESC");

        Assert.Equal(128, forward.Count);

        Assert.Equal(forward.Select(row => row.Input.Child.NodeId), reverse.AsEnumerable().Reverse().Select(row => row.Input.Child.NodeId));

        foreach (var pair in forward.Zip(reverse.AsEnumerable().Reverse()))
        {
            Assert.Equal(pair.First.Input.Vector.ToArray(), pair.Second.Input.Vector.ToArray());

            Assert.Equal(pair.First.Input.Child.SourceDigest, pair.Second.Input.Child.SourceDigest);

            Assert.Equal(JsonSerializer.Serialize(pair.First.Snapshot, LongRestJsonContext.Default.LongRestSnapshot),
                JsonSerializer.Serialize(pair.Second.Snapshot, LongRestJsonContext.Default.LongRestSnapshot));
        }

        var forwardPlan = LongRestSweepHarness.Require(LongRestHierarchy.PlanPage([.. forward.Select(row => row.Input)], 1, recipe, CancellationToken.None));

        var reversePlan = LongRestSweepHarness.Require(LongRestHierarchy.PlanPage([.. reverse.Select(row => row.Input)], 1, recipe, CancellationToken.None));

        Assert.NotEmpty(forwardPlan);

        Assert.Equal(forwardPlan.Length, reversePlan.Length);

        for (int group = 0; group < forwardPlan.Length; group++)
        {
            Assert.Equal(LongRestHierarchy.Membership(forwardPlan[group], recipe), LongRestHierarchy.Membership(reversePlan[group], recipe));

            Assert.Equal(forwardPlan[group].Select(child => child.NodeId), reversePlan[group].Select(child => child.NodeId));

            Assert.Equal(LongRestDiscoveryHash.Work(LongRestHierarchyBuilder.CreateFold(generation, 1, group, forwardPlan[group]).Work).Value,
                LongRestDiscoveryHash.Work(LongRestHierarchyBuilder.CreateFold(generation, 1, group, reversePlan[group]).Work).Value);
        }

        LongRestTarget anchor = forward[0].Input.Child.ExpandedTargets[0];

        float[] direction = SphericalKMeans.NormalizedDirection(forward[0].Input.Vector.ToArray());

        ImmutableArray<LongRestCandidate> forwardCandidates = LongRestSweepHarness.Require(LongRestDiscoveryPolicy.MergeCandidates(anchor,
            ScoreVectors(forward).Concat(await ReadLexicalAsync("ASC", forward))));

        ImmutableArray<LongRestCandidate> reverseCandidates = LongRestSweepHarness.Require(LongRestDiscoveryPolicy.MergeCandidates(anchor,
            ScoreVectors(reverse).Concat(await ReadLexicalAsync("DESC", reverse))));

        Assert.Equal(forwardCandidates.ToArray(), reverseCandidates.ToArray());

        Assert.Equal(8, forwardCandidates.Count(candidate => candidate.Lane == LongRestDiscoveryLane.Lexical));

        Assert.Equal(8, forwardCandidates.Count(candidate => candidate.Lane == LongRestDiscoveryLane.Vector));

        var forwardGroups = LongRestSweepHarness.Require(LongRestDiscoveryPolicy.GroupCandidates(anchor, forwardCandidates));

        var reverseGroups = LongRestSweepHarness.Require(LongRestDiscoveryPolicy.GroupCandidates(anchor, reverseCandidates));

        Assert.Equal(2, forwardGroups.Length);

        Assert.Equal(forwardGroups.Length, reverseGroups.Length);

        Dictionary<string, LongRestSnapshot> snapshots = forward.ToDictionary(row => row.Snapshot.MemoryId, row => row.Snapshot, StringComparer.Ordinal);

        List<string> receiptIdentities = [];

        foreach (var pair in forwardGroups.Zip(reverseGroups))
        {
            Assert.Equal(pair.First.Targets.ToArray(), pair.Second.Targets.ToArray());

            Assert.Equal(pair.First.Evidence.ToArray(), pair.Second.Evidence.ToArray());

            string firstReceipt = LongRestSweepHarness.Require(LongRestPolicy.Identify(
                new(LongRestTransformationKind.EquivalentObservations, [.. pair.First.Targets]),
                [.. pair.First.Targets.Select(target => snapshots[target.MemoryId])]));

            string reversedReceipt = LongRestSweepHarness.Require(LongRestPolicy.Identify(
                new(LongRestTransformationKind.EquivalentObservations, [.. pair.Second.Targets.Reverse()]),
                [.. pair.Second.Targets.Reverse().Select(target => snapshots[target.MemoryId])]));

            Assert.Equal(firstReceipt, reversedReceipt);

            receiptIdentities.Add(firstReceipt);
        }

        Assert.Equal(2, receiptIdentities.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(0, h.SummarySends);

        Assert.Equal(0, h.EmbeddingSends);

        IEnumerable<LongRestCandidate> ScoreVectors(List<(LongRestHierarchyInput Input, LongRestSnapshot Snapshot)> rows) =>
            rows.Select(row => new LongRestCandidate(row.Input.Child.ExpandedTargets[0], LongRestDiscoveryLane.Vector,
                Math.Clamp(SphericalKMeans.DirectionCosine(direction, SphericalKMeans.NormalizedDirection(row.Input.Vector.ToArray())), -1, 1)));

        async Task<List<(LongRestHierarchyInput Input, LongRestSnapshot Snapshot)>> ReadPageAsync(string order)
        {
            List<(LongRestHierarchyChild Child, ImmutableArray<float> Vector, long Sequence, long Epoch, string SnapshotJson)> rows = [];

            await using (SqliteCommand command = h.Connection.CreateCommand())
            {
                command.Transaction = transaction;

                command.CommandText = $"""
                    SELECT node.NodeId, node.Layer, node.GroupOrdinal, node.MembershipDigest, node.SourceDigest,
                        leaf.InsertionSequence, leaf.MutationEpoch, leaf.SnapshotJson, memory.Id, memory.Content,
                        version.VersionId, version.ContentHash, embedding.Dim, embedding.Embedding, leaf.EmbeddingDigest
                    FROM long_rest_discovery_nodes node
                    JOIN long_rest_discovery_leaves leaf ON leaf.GenerationId = node.GenerationId AND leaf.MemoryId = node.MemoryId
                    JOIN saga_memories memory ON memory.Id = leaf.MemoryId
                    JOIN annal_versions version ON version.VersionId = leaf.VersionId
                    JOIN saga_memory_embeddings embedding ON embedding.MemoryId = memory.Id
                    WHERE node.GenerationId = $generation AND node.Layer = 0
                    ORDER BY memory.Id {order};
                    """;

                command.Parameters.AddWithValue("$generation", generation.GenerationId.ToString("D").ToUpperInvariant());

                await using SqliteDataReader reader = await command.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    byte[] rawVector = (byte[])reader[13];

                    Assert.Equal(64, reader.GetInt32(12));

                    Assert.Equal(64 * sizeof(float), rawVector.Length);

                    Assert.Equal((byte[])reader[14], SHA256.HashData(rawVector));

                    ImmutableArray<float> vector = [.. EmbeddingBlobCodec.Decode(rawVector)];

                    LongRestTarget target = new(reader.GetString(8), reader.GetString(10), Convert.ToHexString((byte[])reader[11]));

                    LongRestHierarchyChild child = new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt64(2),
                        new((byte[])reader[3]), new((byte[])reader[4]), reader.GetString(9), ContentSensitivity.None,
                        GenerationProvenance.CreateExact([]), [], [target]);

                    rows.Add((child, vector, reader.GetInt64(5), reader.GetInt64(6), reader.GetString(7)));
                }
            }

            List<(LongRestHierarchyInput Input, LongRestSnapshot Snapshot)> result = [];

            foreach (var row in rows)
            {
                var loaded = LongRestSweepHarness.Require(await SagaLongRestService.ReadSnapshotAsync(h.Connection, transaction,
                    row.Child.ExpandedTargets[0], CancellationToken.None));

                Assert.Equal(row.SnapshotJson, JsonSerializer.Serialize(loaded.Snapshot, LongRestJsonContext.Default.LongRestSnapshot));

                Assert.Equal(row.Child.Content, loaded.Content);

                LongRestSourceEmbeddingBinding binding = new(LongRestSourceEmbeddingKind.ObservedSaga, row.Vector.Length,
                    new(SHA256.HashData(EmbeddingBlobCodec.Encode(row.Vector.ToArray()))));

                LongRestSourceLeaf leaf = new(loaded.Snapshot, row.Sequence, row.Epoch, binding, ContentSensitivity.None,
                    GenerationProvenance.CreateExact([]), [], loaded.Content, row.Vector);

                Assert.Equal(row.Child.SourceDigest, LongRestDiscoveryHash.SourcePage(generation.Scope, [leaf]).Value);

                Assert.Equal(row.Child.SourceDigest, row.Child.MembershipDigest);

                result.Add((new(row.Child, row.Vector), loaded.Snapshot));
            }

            return result;
        }

        async Task<List<LongRestCandidate>> ReadLexicalAsync(string order, List<(LongRestHierarchyInput Input, LongRestSnapshot Snapshot)> rows)
        {
            Dictionary<string, LongRestTarget> targets = rows.ToDictionary(row => row.Snapshot.MemoryId, row => row.Input.Child.ExpandedTargets[0], StringComparer.Ordinal);

            List<LongRestCandidate> result = [];

            await using SqliteCommand command = h.Connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"""
                SELECT node.MemoryId, bm25(long_rest_clean_fts) FROM long_rest_clean_fts
                JOIN long_rest_discovery_nodes node ON node.NodeId = long_rest_clean_fts.NodeId AND node.GenerationId = long_rest_clean_fts.GenerationId
                WHERE long_rest_clean_fts MATCH $query AND node.GenerationId = $generation AND node.Layer = 0
                ORDER BY node.MemoryId {order};
                """;

            command.Parameters.AddWithValue("$query", "\"opal\"");

            command.Parameters.AddWithValue("$generation", generation.GenerationId.ToString("D").ToUpperInvariant());

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new(targets[reader.GetString(0)], LongRestDiscoveryLane.Lexical, reader.GetDouble(1)));
            }

            return result;
        }
    }

}
