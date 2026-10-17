using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestHierarchyTests
{
    /// <summary>
    /// The corpus is built so that the folds the test reads cannot depend on the clustering seed. A page of leaves is clustered from a seed
    /// hashed out of its leaves' membership digests, and those bind the claim and version identifiers drawn afresh for every memory, so the
    /// same corpus is clustered from a different seed in every run (and always from the same one within a generation). Memories whose
    /// vectors lie near one another leave that clustering nothing to separate: which clusters the seed draws, and so which folds come
    /// first and how many leaves they hold, then changes from run to run (one cluster of 114 leaves beside seven of two was the commonest
    /// shape it produced), and a lane filled from folds of two leaves is short of 32. Here the memories come in groups of sixteen, the width of
    /// a fold; the members of a group are one point, and every group lies along an axis of its own. A page of 128 leaves holds eight
    /// groups on eight different axes, so its layer asks for eight clusters from eight distinct points. An axis is its own normalization,
    /// so a point's dot product with a copy of itself is exactly 1 and with another axis exactly 0. K-means++ weights a point by its
    /// distance to the nearest centroid, which is exactly zero for a copy of a centroid, so it seeds one centroid in every group whatever
    /// the seed, and each point is then exactly as near its own centroid as a point can be and exactly as far from the others. The
    /// fold at position <c>n</c> is therefore group <c>n</c> in every run, which the test states before it relies on it. The recall tests
    /// build their groups the same way.
    /// </summary>
    [SkippableTheory]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(129, false)]
    [InlineData(129, true)]
    public async Task Actual_hierarchy_scan_keeps_the_top_k_summaries_off_the_anchors_chain_and_interleaves_their_leaves(int summaries, bool reverse)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        const int groupSize = LongRestDiscoveryLimits.HierarchyChildren;

        int[] sources = [.. Enumerable.Range(0, summaries * groupSize)];

        foreach (int index in reverse ? sources.Reverse() : sources)
        {
            string memory = $"rank-{index:D5}";

            await h.SeedAsync(memory, $"unique ranked observation {index:D5}");

            // Group 64 comes back to the first axis, but the groups of one page are eight consecutive numbers and none of them repeats.
            await h.SetEmbeddingAsync(memory, Axis(LongRestClusters.Width, index / groupSize % LongRestClusters.Width));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        List<(Guid Node, double Score, LongRestTarget[] Leaves)> candidates = [];

        float[] query = new float[64];

        query[0] = 1;

        for (int index = 0; index < summaries; index++)
        {
            LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None))!;

            Assert.Equal(1, work.Fold.Layer);

            // The shape everything below rests on, stated where it is read: fold n is exactly group n, all sixteen of its leaves in memory id
            // order. The groups together are the whole corpus, so no other layer-1 fold exists, and a clustering that no longer parts the
            // groups this way fails here instead of handing the selection a lucky or an unlucky corpus.
            Assert.Equal(groupSize, work.Fold.Children.Length);

            Assert.Equal([.. Enumerable.Range(index * groupSize, groupSize).Select(static number => $"rank-{number:D5}")], LongRestSweepHarness.MemoriesOf(work));

            // Both canonical Guid halves exercise unsigned high-bit transitions. Reversing
            // assignment changes insertion versus persisted Guid page order without changing SQL.
            int identity = reverse ? summaries - index : index + 1;

            string[] high = ["00000000", "7fffffff", "80000000", "ffffffff"];

            Guid node = Guid.Parse($"{high[identity % 4]}-0000-{identity:x4}-{(identity % 2 == 0 ? "8000" : "7fff")}-{identity:x12}");

            LongRestAcceptedHierarchySummary summary = await h.SummaryAsync(work, node);

            LongRestHierarchyPublication publication = await h.EmbeddingAsync(work, summary);

            float[] vector = new float[64];

            vector[0] = 1;

            vector[1] = index % 3 * 0.125f;

            // The generated payload, value digest and paid association are committed together;
            // no persisted node, evidence, foreign key or schema guard is rewritten afterwards.
            publication = publication with
            {
                Vector = [.. vector],
                EmbeddingBinding = publication.EmbeddingBinding with
                {
                    ValueDigest = new(System.Security.Cryptography.SHA256.HashData(EmbeddingBlobCodec.Encode(vector))),
                },
            };

            LongRestSweepHarness.Require(await h.Store.CommitFoldAsync(publication, null, CancellationToken.None));

            candidates.Add((node, Math.Clamp(SphericalKMeans.DirectionCosine(query, SphericalKMeans.NormalizedDirection(vector)), -1, 1),
                [.. work.Fold.Children.SelectMany(static child => child.ExpandedTargets)]));
        }

        LongRestTarget anchor = candidates[0].Leaves[0];

        PriorityQueue<(Guid Node, double Score, LongRestTarget[] Leaves), (Guid Node, double Score)> original = new(
            Comparer<(Guid Node, double Score)>.Create(static (left, right) =>
            {
                int score = left.Score.CompareTo(right.Score);

                return score != 0 ? score : -StringComparer.Ordinal.Compare(left.Node.ToString("D"), right.Node.ToString("D"));
            }));

        // The anchor's own fold is its nearest, and here its only, ancestor: it is the query, it is on the chain, and it is never ranked.
        foreach (var candidate in candidates.Skip(1))
        {
            original.Enqueue(candidate, (candidate.Node, candidate.Score));

            if (original.Count > LongRestDiscoveryLimits.SummaryTopK) original.Dequeue();
        }

        var selected = original.UnorderedItems.Select(static item => item.Element)
            .OrderByDescending(static item => item.Score).ThenBy(static item => item.Node.ToString("D"), StringComparer.Ordinal).ToArray();

        // The lane takes one leaf from each selected summary in turn, best summary first, until it holds 32. Written out longhand: this
        // is the specification the production interleave is held to.
        List<LongRestCandidate> expected = [];

        for (int round = 0; round < LongRestDiscoveryLimits.ExpandedLeaves && expected.Count < LongRestDiscoveryLimits.MergedNeighbors; round++)
        {
            foreach (var item in selected)
            {
                if (round < item.Leaves.Length && expected.Count < LongRestDiscoveryLimits.MergedNeighbors)
                {
                    expected.Add(new(item.Leaves[round], LongRestDiscoveryLane.Hierarchy, item.Score, expected.Count));
                }
            }
        }

        await using var transaction = h.Connection.BeginTransaction();

        // Invoke the actual private scan so its hierarchy lane can be compared before the
        // unrelated raw-vector/lexical lanes merge it. All node reads, paging and expansion run.
        var method = typeof(LongRestDiscoveryStore).GetMethod("HierarchyCandidatesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var operation = (Task<System.Collections.Immutable.ImmutableArray<LongRestCandidate>>)method.Invoke(null,
            [h.Connection, transaction, generation, anchor, null, CancellationToken.None])!;

        var actual = await operation;

        Assert.Equal(expected.ToArray(), actual.ToArray());

        Assert.Equal(LongRestDiscoveryLimits.MergedNeighbors, actual.Length);

        Assert.Equal(summaries, candidates.Count);

        Assert.Equal(summaries, h.SummarySends);

        Assert.Equal(summaries, h.EmbeddingSends);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generated_singleton_keeps_paid_identity_through_mixed_layers_or_depth_limit(bool atDepthLimit)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        // Identical vectors make every layer degenerate, which is the shape (seventeen full folds, then the rest) the counts below pin.
        h.IdenticalVectors = true;

        for (int index = 0; index < 272; index++)
        {
            await h.SeedAsync($"m-{index:D3}", $"unique observation {index:D3}");
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        for (int fold = 0; fold < 17; fold++)
        {
            LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None))!;

            Assert.Equal(1, work.Fold.Layer);

            LongRestAcceptedHierarchySummary accepted = await h.SummaryAsync(work);

            LongRestSweepHarness.Require(await h.Store.CommitFoldAsync(await h.EmbeddingAsync(work, accepted), null, CancellationToken.None));
        }

        const string originalEvidence = """
            SELECT group_concat(EvidenceJson) FROM (
                SELECT accepted.EvidenceJson FROM long_rest_accepted_summaries accepted
                JOIN long_rest_discovery_nodes node ON node.NodeId = accepted.NodeId WHERE node.Layer = 1 ORDER BY node.NodeId);
            """;

        object? original = await h.ScalarAsync(originalEvidence);

        if (atDepthLimit)
        {
            // Exercise the sixth-level durable boundary with real paid lower-layer nodes. The
            // ordinary branch independently proves the natural whole-build mixed-layer path.
            await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET FoldLayer = 6, FoldGroup = 0;");
        }

        LongRestDiscoveryGeneration published = await h.BuildAsync(generation);

        Assert.True(published.Published);

        Assert.Equal(atDepthLimit ? 18 : 19, h.SummarySends);

        Assert.Equal(h.SummarySends, h.EmbeddingSends);

        Assert.Equal(original, await h.ScalarAsync(originalEvidence));

        Assert.Equal(atDepthLimit ? 6L : 4L, await h.ScalarAsync("SELECT FoldLayer FROM long_rest_discovery_generations;"));

        const string roots = """
            SELECT group_concat(NodeId) FROM (SELECT NodeId FROM long_rest_discovery_nodes node WHERE NOT EXISTS (
                SELECT 1 FROM long_rest_discovery_edges edge WHERE edge.ChildNodeId = node.NodeId) ORDER BY NodeId);
            """;

        string persistedRoots = (string)(await h.ScalarAsync(roots))!;

        Assert.Equal(atDepthLimit ? 2 : 1, persistedRoots.Split(',').Length);

        if (!atDepthLimit)
        {
            Assert.Equal(1L, await h.ScalarAsync("""
                SELECT count(*) FROM long_rest_discovery_nodes parent WHERE parent.Layer = 3
                    AND EXISTS (SELECT 1 FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes child ON child.NodeId = edge.ChildNodeId
                        WHERE edge.ParentNodeId = parent.NodeId AND child.Layer = 1)
                    AND EXISTS (SELECT 1 FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes child ON child.NodeId = edge.ChildNodeId
                        WHERE edge.ParentNodeId = parent.NodeId AND child.Layer = 2);
                """));
        }

        await using var reopened = h.Saga.CreateSiblingContext();

        LongRestDiscoveryStore fresh = new(reopened, TimeProvider.System);

        Assert.True((await fresh.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)).IsFailure);

        Assert.Equal(persistedRoots, await h.ScalarAsync(roots));

        Assert.Equal(atDepthLimit ? 18 : 19, h.SummarySends);
    }

    [SkippableTheory]
    [InlineData("content")]
    [InlineData("pin")]
    [InlineData("retirement")]
    [InlineData("vector")]
    public async Task Accepted_stage_cannot_be_reused_after_a_bound_source_change(string mutation)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("a", "first observation");

        await h.SeedAsync("b", "second observation");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None))!;

        _ = await h.SummaryAsync(work);

        await h.ExecuteAsync(mutation switch
        {
            "content" => "UPDATE saga_memories SET Content = 'changed' WHERE Id = 'a';",

            "pin" => "UPDATE saga_memories SET PinnedAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = 'a';",

            "retirement" => "UPDATE saga_memories SET RetiredAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = 'a';",

            _ => "UPDATE saga_memory_embeddings SET Embedding = zeroblob(256) WHERE MemoryId = 'a';",
        });

        Assert.True((await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)).IsFailure);

        Assert.Equal(1, h.SummarySends);

        Assert.Equal(0, h.EmbeddingSends);
    }

    /// <summary>
    /// A request lists the first few leaves of each child, but the summary it asks for, and the embedding of that summary after it, derive from
    /// every leaf under the children. The fold names all of them, so the gates in front of its sends can re-prove each one.
    /// </summary>
    [SkippableFact]
    public async Task A_fold_names_every_leaf_under_its_children_however_deep()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int index = 0; index < 300; index++)
        {
            await h.SeedAsync($"leaf-{index:D4}", $"observation {index:D4} about orchard apple harvest");
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        int deepest = 0;

        bool beyondTheRequest = false;

        while (LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)) is { } work)
        {
            List<string> expected = [];

            foreach (LongRestHierarchyChild child in work.Fold.Children)
            {
                await using SqliteCommand walk = h.Connection.CreateCommand();

                walk.CommandText = """
                    WITH RECURSIVE subtree(NodeId) AS (
                        SELECT $root UNION ALL SELECT edge.ChildNodeId FROM long_rest_discovery_edges edge JOIN subtree ON edge.ParentNodeId = subtree.NodeId)
                    SELECT node.MemoryId FROM subtree JOIN long_rest_discovery_nodes node ON node.NodeId = subtree.NodeId WHERE node.Layer = 0;
                    """;

                walk.Parameters.AddWithValue("$root", child.NodeId.ToString("D").ToUpperInvariant());

                await using SqliteDataReader reader = await walk.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    expected.Add(reader.GetString(0));
                }
            }

            Assert.Equal(expected.Order(StringComparer.Ordinal), work.Leaves.Order(StringComparer.Ordinal));

            Assert.Equal(work.Leaves.Length, work.Leaves.Distinct().Count());

            deepest = Math.Max(deepest, work.Fold.Layer);

            beyondTheRequest |= work.Leaves.Length > work.Fold.Children.Sum(static child => child.ExpandedTargets.Length);

            LongRestAcceptedHierarchySummary summary = await h.SummaryAsync(work);

            LongRestSweepHarness.Require(await h.Store.CommitFoldAsync(await h.EmbeddingAsync(work, summary), null, CancellationToken.None));
        }

        Assert.True(deepest >= 3, $"The forest is {deepest} layers deep, so no fold has a child whose subtree is deeper than its request lists.");

        Assert.True(beyondTheRequest, "No fold stood for more leaves than its request lists.");
    }

    [SkippableFact]
    public async Task Complete_publication_rechecks_paid_generated_nodes()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("a", "first observation");

        await h.SeedAsync("b", "second observation");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None))!;

        LongRestAcceptedHierarchySummary summary = await h.SummaryAsync(work);

        LongRestSweepHarness.Require(await h.Store.CommitFoldAsync(await h.EmbeddingAsync(work, summary), null, CancellationToken.None));

        Assert.Null(LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None)));

        await h.ExecuteAsync("UPDATE long_rest_discovery_embeddings SET PaidEvidenceJson = '{}';");

        long revision = (long)(await h.ScalarAsync("SELECT Revision FROM long_rest_discovery_generations;"))!;

        Assert.True((await h.Store.PublishAsync(generation.GenerationId, revision, generation.Fence, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await h.ScalarAsync("SELECT Published FROM long_rest_discovery_generations;"));
    }
}
