using System.Collections.Immutable;
using System.Diagnostics.Metrics;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Ancestor-Excluded Cross-Cluster Recall. The hierarchy lane queries with the anchor's nearest generated ancestor, never ranks,
// selects or expands a node on the anchor's own ancestor chain, and fills its lane by taking leaves from the selected summaries in
// turn. None of these tests depends on how the clusterer happens to partition leaves it cannot tell apart: every group below is one
// direction repeated (so the clusterer cannot split or merge it), and a fold's summary vector is chosen by the test.
public sealed partial class LongRestHierarchyTests
{
    private static float[] Axis(int width, int index)
    {
        float[] vector = new float[width];

        vector[index] = 1;

        return vector;
    }

    // A vector at the given cosine to Axis(width, 0), tilted toward another axis.
    private static float[] Tilted(int width, int axis, double cosine)
    {
        float[] vector = new float[width];

        vector[0] = (float)cosine;

        vector[axis] = (float)Math.Sqrt(1 - cosine * cosine);

        return vector;
    }

    // One group of identical raw vectors per prefix, sixteen memories each, every memory with a token of its own.
    private static async Task SeedGroupsAsync(LongRestSweepHarness h, params string[] prefixes)
    {
        for (int group = 0; group < prefixes.Length; group++)
        {
            for (int index = 0; index < 16; index++)
            {
                string memory = $"{prefixes[group]}-{index:D2}";

                await h.SeedAsync(memory, $"observation {prefixes[group]}token{index:D2}");

                await h.SetEmbeddingAsync(memory, Axis(prefixes.Length, group));
            }
        }
    }

    [SkippableFact]
    public async Task Hierarchy_recall_never_returns_evidence_through_the_anchors_own_ancestor_chain()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await SeedGroupsAsync(h, "g0", "g1", "g2", "g3");

        // Every summary of every layer is the same direction, so every node, the anchor's own ancestors included, resembles the
        // query exactly. Only the exclusion of the chain can keep the anchor's own tree out of the lane.
        h.FoldVector = _ => Axis(64, 0);

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.BuildAsync(generation);

        Assert.Equal(4L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 1;"));

        Assert.True((long)(await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 2;"))! >= 1);

        ImmutableArray<LongRestCandidate> lane = await h.HierarchyLaneAsync(generation, await h.TargetAsync("g0-00"));

        // Foreign summaries are reached; the anchor's own fold, which only its parent and the layers above can expand to, is not.
        Assert.NotEmpty(lane);

        Assert.All(lane, static candidate => Assert.Equal(LongRestDiscoveryLane.Hierarchy, candidate.Lane));

        Assert.All(lane, static candidate => Assert.DoesNotMatch("^g0-", candidate.Target.MemoryId));

        Assert.Equal(32, lane.Length);
    }

    // Excluding the anchor's own chain from ranking must not exempt it from verification: the root and every ancestor between the anchor and
    // the root are read and checked on every discovery, so a record altered anywhere on the chain refuses the anchor, not only the record
    // of its nearest ancestor. Seventeen groups of sixteen make a layer-1 node each; sixteen of those fold into a layer-2 node and the
    // seventeenth is carried, so an anchor of the first group has three generated ancestors: layer 1, layer 2 and the layer-3 root.
    [SkippableTheory]
    [InlineData("none")]
    [InlineData("nearest-summary")]
    [InlineData("intermediate-summary")]
    [InlineData("intermediate-embedding")]
    [InlineData("root-summary")]
    [InlineData("root-embedding")]
    [InlineData("foreign-summary")]
    public async Task Discovery_refuses_an_anchor_whose_ancestor_chain_was_altered_anywhere(string tamper)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        string[] prefixes = [.. Enumerable.Range(0, 17).Select(static index => $"g{index:D2}")];

        await SeedGroupsAsync(h, prefixes);

        h.FoldVector = _ => Axis(64, 0);

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.BuildAsync(generation);

        Assert.Equal(17L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 1;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 2;"));

        Assert.Equal(1L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 3;"));

        string anchor = "g00-00";

        string nearest = "(SELECT parent.NodeId FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes parent ON parent.NodeId = edge.ParentNodeId "
            + $"JOIN long_rest_discovery_nodes leaf ON leaf.NodeId = edge.ChildNodeId WHERE leaf.Layer = 0 AND leaf.MemoryId = '{anchor}')";

        string foreign = "(SELECT parent.NodeId FROM long_rest_discovery_edges edge JOIN long_rest_discovery_nodes parent ON parent.NodeId = edge.ParentNodeId "
            + "JOIN long_rest_discovery_nodes leaf ON leaf.NodeId = edge.ChildNodeId WHERE leaf.Layer = 0 AND leaf.MemoryId = 'g05-00')";

        string statement = tamper switch
        {
            "none" => "SELECT 1;",

            "nearest-summary" => $"UPDATE long_rest_discovery_nodes SET Summary = 'altered' WHERE NodeId = {nearest};",

            "intermediate-summary" => "UPDATE long_rest_discovery_nodes SET Summary = 'altered' WHERE Layer = 2;",

            "intermediate-embedding" => "UPDATE long_rest_discovery_embeddings SET ValueDigest = zeroblob(32) WHERE NodeId IN (SELECT NodeId FROM long_rest_discovery_nodes WHERE Layer = 2);",

            "root-summary" => "UPDATE long_rest_discovery_nodes SET Summary = 'altered' WHERE Layer = 3;",

            "root-embedding" => "UPDATE long_rest_discovery_embeddings SET ValueDigest = zeroblob(32) WHERE NodeId IN (SELECT NodeId FROM long_rest_discovery_nodes WHERE Layer = 3);",

            _ => $"UPDATE long_rest_discovery_nodes SET Summary = 'altered' WHERE NodeId = {foreign};",
        };

        await h.ExecuteAsync(statement);

        Result<LongRestCandidateGroup[]> discovered = await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), null, CancellationToken.None);

        Assert.Equal(tamper == "none", discovered.IsSuccess);
    }

    [SkippableFact]
    public async Task An_isolated_anchor_with_no_similar_foreign_summary_yields_no_group()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int index = 0; index < 257; index++)
        {
            string memory = $"m-{index:D3}";

            await h.SeedAsync(memory, $"uniquetoken{index:D3}");

            await h.SetEmbeddingAsync(memory, Axis(257, index));
        }

        // Each fold, at every layer, gets a direction of its own, so no summary resembles any other.
        int next = 0;

        h.FoldVector = _ => Axis(64, next++);

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestDiscoveryGeneration published = await h.BuildAsync(generation);

        Assert.True(published.Published);

        Assert.True((long)(await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 1;"))! > 0);

        foreach (string anchor in new[] { "m-000", "m-128", "m-256" })
        {
            Assert.Empty(LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), null, CancellationToken.None)));
        }
    }

    [SkippableFact]
    public async Task Hierarchy_only_recall_crosses_source_pages_through_a_similar_foreign_summary()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        // 130 memories fill one source page of 128 and put two more on the next. All are mutually orthogonal and share no token.
        for (int index = 0; index < 130; index++)
        {
            string memory = $"m-{index:D3}";

            await h.SeedAsync(memory, $"uniquetoken{index:D3}");

            await h.SetEmbeddingAsync(memory, Axis(130, index));
        }

        // The anchor's parent is Axis; the fold of the second page resembles it at cosine 0.97; every other summary resembles nothing.
        int other = 2;

        h.FoldVector = work =>
        {
            string[] memories = LongRestSweepHarness.MemoriesOf(work);

            if (work.Fold.Layer == 1 && memories.Contains("m-000", StringComparer.Ordinal)) return Axis(64, 0);

            if (work.Fold.Layer == 1 && memories.Contains("m-128", StringComparer.Ordinal)) return Tilted(64, 1, 0.97);

            return Axis(64, other++);
        };

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestDiscoveryGeneration published = await h.BuildAsync(generation);

        Assert.True(published.Published);

        Assert.Equal(130L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 0;"));

        Dictionary<string, List<int>> measurements = new(StringComparer.Ordinal)
        {
            ["arcanum.long_rest.discovery.selected_summaries"] = [],

            ["arcanum.long_rest.discovery.frontier"] = [],

            ["arcanum.long_rest.discovery.expanded_leaves"] = [],
        };

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observed) =>
        {
            if (instrument.Meter.Name == "Arcanum" && measurements.ContainsKey(instrument.Name))
            {
                observed.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
        {
            Assert.Empty(tags.ToArray());

            measurements[instrument.Name].Add(measurement);
        });

        listener.Start();

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("m-000"), null, CancellationToken.None));

        // The anchor reaches the other page's fold through its similar summary, and nothing else: no lexical or vector evidence exists.
        LongRestCandidateGroup group = Assert.Single(groups);

        Assert.Equal(["m-000", "m-128", "m-129"], group.Targets.Select(static target => target.MemoryId));

        Assert.All(group.Evidence, static candidate => Assert.Equal(LongRestDiscoveryLane.Hierarchy, candidate.Lane));

        Assert.All(group.Evidence, static candidate => Assert.InRange(candidate.Score, 0.969, 0.971));

        Assert.Equal(1, Assert.Single(measurements["arcanum.long_rest.discovery.selected_summaries"]));

        Assert.Equal(2, Assert.Single(measurements["arcanum.long_rest.discovery.expanded_leaves"]));

        Assert.NotEmpty(measurements["arcanum.long_rest.discovery.frontier"]);

        Assert.All(measurements["arcanum.long_rest.discovery.frontier"], static value => Assert.InRange(value, 0, 128));
    }

    [SkippableFact]
    public async Task Hierarchy_expansion_interleaves_selected_summaries_without_memory_id_bias()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        // The anchor's group has the highest ids and the best-resembling foreign group the next highest: if the lane were filled by
        // summary score and then memory id, 'd' (0.99) and 'c' (0.97) would hold all 32 places and 'b' and 'a' none.
        string[] prefixes = ["e", "d", "c", "b", "a"];

        await SeedGroupsAsync(h, prefixes);

        double[] cosines = [1, 0.99, 0.97, 0.95, 0.93];

        h.FoldVector = work =>
        {
            if (work.Fold.Layer > 1) return Axis(64, 20 + work.Fold.Layer);

            int group = Array.IndexOf(prefixes, LongRestSweepHarness.MemoriesOf(work)[0][..1]);

            return group == 0 ? Axis(64, 0) : Tilted(64, group, cosines[group]);
        };

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.BuildAsync(generation);

        Assert.Equal(5L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 1;"));

        ImmutableArray<LongRestCandidate> lane = await h.HierarchyLaneAsync(generation, await h.TargetAsync("e-00"));

        Assert.Equal(32, lane.Length);

        Assert.Equal(Enumerable.Range(0, 32), lane.Select(static candidate => candidate.Rank));

        // Round r takes the r-th leaf of each selected summary in score order: d, c, b, a, d, c, b, a, ...
        string[] expectedGroups = [.. Enumerable.Range(0, 32).Select(index => prefixes[1 + index % 4])];

        Assert.Equal(expectedGroups, lane.Select(static candidate => candidate.Target.MemoryId[..1]));

        Assert.Equal(Enumerable.Range(0, 32).Select(static index => $"{(char)('d' - index % 4)}-{index / 4:D2}"), lane.Select(static candidate => candidate.Target.MemoryId));

        // Each leaf keeps the score of the summary that supplied it, and the lane is not in memory id order.
        Assert.All(lane.Select((candidate, index) => (candidate, index)), item => Assert.InRange(item.candidate.Score, cosines[1 + item.index % 4] - 1e-4, cosines[1 + item.index % 4] + 1e-4));

        Assert.NotEqual(lane.Select(static candidate => candidate.Target.MemoryId).Order(StringComparer.Ordinal), lane.Select(static candidate => candidate.Target.MemoryId));

        // The same lane reaches the merged groups: fifteen evidence per group beside the anchor, taken in the lane's order.
        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("e-00"), null, CancellationToken.None));

        Assert.Contains(groups.SelectMany(static group => group.Evidence), static candidate => candidate.Lane == LongRestDiscoveryLane.Hierarchy && candidate.Target.MemoryId.StartsWith('a'));
    }
}
