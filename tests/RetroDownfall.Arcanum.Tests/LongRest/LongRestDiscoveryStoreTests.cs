using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestDiscoveryStoreTests
{
    [SkippableFact]
    public async Task A_vector_neighbor_survives_its_later_eviction_from_the_lexical_lane()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("anchor", "opal");

        await h.SeedAsync("a-vector", "opal " + string.Join(' ', Enumerable.Repeat("filler", 20)));

        for (int index = 0; index < 9; index++)
        {
            string memory = $"b-lexical-{index}";

            await h.SeedAsync(memory, "opal gem");

            float[] vector = new float[64];

            vector[1] = 1;

            await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Embedding = $vector WHERE MemoryId = $memory;",
                ("$vector", EmbeddingBlobCodec.Encode(vector)), ("$memory", memory));
        }

        // Unrelated memories, each with a vector of its own, so 'opal' is a distinctive token: eleven of fifty documents.
        for (int index = 0; index < 39; index++)
        {
            string memory = $"c-filler-{index:D2}";

            await h.SeedAsync(memory, $"unrelated{index} filler{index}");

            float[] vector = new float[64];

            vector[2 + index % 60] = 1;

            await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Embedding = $vector WHERE MemoryId = $memory;",
                ("$vector", EmbeddingBlobCodec.Encode(vector)), ("$memory", memory));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        Assert.Contains(groups.SelectMany(group => group.Evidence), candidate => candidate.Target.MemoryId == "a-vector" && candidate.Lane == LongRestDiscoveryLane.Vector);

        // The nine short matches fill the lexical lane; the longer memory, which matches too and clears the floor, scores lower and is not among them.
        Assert.DoesNotContain(groups.SelectMany(group => group.Evidence), candidate => candidate.Target.MemoryId == "a-vector" && candidate.Lane == LongRestDiscoveryLane.Lexical);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lexical_uses_token_matches_and_scoped_scores_with_deterministic_ties(bool accelerated)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        h.Saga.VectorAccelerator.SetAvailable(accelerated);

        await h.SeedAsync("anchor", "opal OR quartz");

        // Token matches that cover what the anchor says ('or' is a word of the anchor here, never an operator of the query).
        await h.SeedAsync("z-token", "opal or quartz gem");

        await h.SeedAsync("a-token", "opal or quartz gem");

        await h.SeedAsync("substring", "opalescent quarry");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        // This test isolates read-side discovery from the separately tested hierarchy publisher.
        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        LongRestCandidate[] lexical = groups.SelectMany(group => group.Evidence).Where(candidate => candidate.Lane == LongRestDiscoveryLane.Lexical).ToArray();

        Assert.Equal(["a-token", "z-token"], lexical.Select(candidate => candidate.Target.MemoryId));

        Assert.All(lexical, candidate => Assert.True(candidate.Score < 0));
    }

    [SkippableFact]
    public async Task Pinned_matches_cannot_displace_an_eligible_neighbor_before_top_k()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("anchor", "amethyst gem");

        for (int index = 0; index < 12; index++)
        {
            await h.SeedAsync($"a-pinned-{index:D2}", "amethyst gem gem");
        }

        await h.SeedAsync("z-eligible", "amethyst eligible");

        // Unrelated memories, and a vector of its own for every memory, so only the lexical lane can name a neighbour.
        for (int index = 0; index < 30; index++)
        {
            await h.SeedAsync($"m-filler-{index:D2}", $"unrelated{index} filler{index}");
        }

        string[] every = ["anchor", .. Enumerable.Range(0, 12).Select(static index => $"a-pinned-{index:D2}"), "z-eligible", .. Enumerable.Range(0, 30).Select(static index => $"m-filler-{index:D2}")];

        for (int index = 0; index < every.Length; index++)
        {
            float[] vector = new float[every.Length];

            vector[index] = 1;

            await h.SetEmbeddingAsync(every[index], vector);
        }

        await h.ExecuteAsync("UPDATE saga_memories SET PinnedAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id LIKE 'a-pinned-%';");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        Assert.Equal(["z-eligible"], groups.SelectMany(group => group.Evidence).Select(candidate => candidate.Target.MemoryId));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vector_candidates_require_equal_width_before_top_k(bool accelerated)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        h.Saga.VectorAccelerator.SetAvailable(accelerated);

        await h.SeedAsync("anchor", "opal");

        for (int index = 0; index < 12; index++)
        {
            string memory = $"a-incompatible-{index:D2}";

            await h.SeedAsync(memory, $"independenttoken{index}");

            await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Dim = 2, Embedding = $vector WHERE MemoryId = $memory;",
                ("$vector", EmbeddingBlobCodec.Encode([1, 0])), ("$memory", memory));
        }

        await h.SeedAsync("z-similar", "sapphire");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidate[] candidates = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None))
            .SelectMany(group => group.Evidence).ToArray();

        LongRestCandidate candidate = Assert.Single(candidates);

        Assert.Equal("z-similar", candidate.Target.MemoryId);

        Assert.Equal(LongRestDiscoveryLane.Vector, candidate.Lane);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unusable_raw_source_vectors_refuse_capture_without_advancing_the_cursor(int corruption)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("anchor", "opal");

        float[] vector = new float[64];

        vector[0] = corruption switch { 1 => float.NaN, 2 => float.PositiveInfinity, _ => 0 };

        await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Embedding = $vector WHERE MemoryId = 'anchor';", ("$vector", EmbeddingBlobCodec.Encode(vector)));

        LongRestDiscoveryGeneration generation = await h.StartAsync();

        Assert.True((await h.Store.CaptureNextSourcePageAsync(generation.GenerationId, generation.Revision, null, CancellationToken.None)).IsFailure);

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT Revision FROM long_rest_discovery_generations;"));
    }

    // Every memory here is given one and the same embedding, so the vector lane has nothing to tell them apart by: the space is collapsed. The
    // anchor's Background Floor is the highest, which pairs nothing but byte-identical content, and so no memory is nominated by its vector
    // whatever the cosine of a vector with itself comes out as.
    [SkippableTheory]
    [InlineData(6)]
    [InlineData(20)]
    public async Task A_collapsed_generation_nominates_no_vector_neighbor_and_records_the_highest_floor(int others)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        List<string> memories = ["anchor", "b-twin"];

        await h.SeedAsync("anchor", "opal quartz");

        await h.SeedAsync("b-twin", "opal quartz");

        for (int index = 0; index < others; index++)
        {
            string memory = $"c-other-{index:D2}";

            memories.Add(memory);

            await h.SeedAsync(memory, $"unrelated{index} filler{index}");
        }

        foreach (string memory in memories)
        {
            await h.SetEmbeddingAsync(memory, h.Saga.Embedding());
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        Assert.NotEmpty(groups);

        Assert.All(groups, group => Assert.Equal(1000, group.PairingFloorPermille));

        LongRestCandidate[] evidence = [.. groups.SelectMany(static group => group.Evidence)];

        Assert.Equal(["b-twin"], evidence.Select(static candidate => candidate.Target.MemoryId));

        Assert.DoesNotContain(evidence, static candidate => candidate.Lane == LongRestDiscoveryLane.Vector);
    }

    private static async Task<LongRestCandidateGroup[]> DiscoverWithinAsync(LongRestSweepHarness h, Guid generation, LongRestTarget anchor, LongRestSpreadCache? spreads)
    {
        await using Microsoft.Data.Sqlite.SqliteTransaction transaction = h.Connection.BeginTransaction(deferred: true);

        return LongRestSweepHarness.Require(await LongRestDiscoveryStore.DiscoverWithinAsync(h.Connection, transaction, generation, anchor, null, CancellationToken.None, spreads));
    }

    private static string[] Shown(LongRestCandidateGroup[] groups) =>
        [.. groups.Select(static group => $"{group.PairingFloorPermille}: " + string.Join(", ", group.Evidence.Select(static candidate => $"{candidate.Target.MemoryId}/{candidate.Lane}/{candidate.Score:R}")))];

    // The sweep reads one anchor to a transaction. The spread of the generation's sample is the same for each of them, so the first one reads it
    // from the leaves and the next are handed it: they read the floor they would have read, and what they are handed is what they read it from.
    [SkippableFact]
    public async Task The_anchors_of_a_generation_share_the_spread_the_first_of_them_read_and_a_remembered_spread_is_what_the_floor_comes_from()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        // A cluster of three among forty memories that each have a vector of their own, and no word in common.
        for (int index = 0; index < 43; index++)
        {
            await h.SeedAsync($"m-{index:D2}", $"unrelated{index} filler{index}");

            await h.SetEmbeddingAsync($"m-{index:D2}", LongRestClusters.Vector([3], index + 1));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestTarget first = await h.TargetAsync("m-00");

        LongRestTarget second = await h.TargetAsync("m-01");

        LongRestSpreadCache cache = new();

        Assert.Null(cache.Share(generation.GenerationId, LongRestClusters.Width, null));

        string[] firstAlone = Shown(await DiscoverWithinAsync(h, generation.GenerationId, first, null));

        string[] firstRemembering = Shown(await DiscoverWithinAsync(h, generation.GenerationId, first, cache));

        Assert.Equal(firstAlone, firstRemembering);

        // The first anchor of the width left what it read.
        double spread = cache.Share(generation.GenerationId, LongRestClusters.Width, null)!.Value;

        Assert.True(double.IsFinite(spread) && spread > 0);

        string[] secondAlone = Shown(await DiscoverWithinAsync(h, generation.GenerationId, second, null));

        Assert.Equal(secondAlone, Shown(await DiscoverWithinAsync(h, generation.GenerationId, second, cache)));

        // The cluster is what the vector lane nominates for the second anchor, so the comparison above says something.
        Assert.Contains(secondAlone, static line => line.Contains("m-00/Vector", StringComparison.Ordinal) && line.Contains("m-02/Vector", StringComparison.Ordinal));

        // Another generation, or another width, has not been read: nothing is taken from this one.
        Assert.Null(cache.Share(Guid.NewGuid(), LongRestClusters.Width, null));

        Assert.Null(cache.Share(generation.GenerationId, LongRestClusters.Width + 1, null));

        // What is remembered is what a hit reads the floor from: a spread that is no number gives the highest floor, and the vector lane then
        // nominates nothing, where the spread this generation really has nominates the cluster.
        LongRestSpreadCache unreadable = new();

        Assert.True(double.IsNaN(unreadable.Share(generation.GenerationId, LongRestClusters.Width, double.NaN)!.Value));

        LongRestCandidateGroup[] refused = await DiscoverWithinAsync(h, generation.GenerationId, second, unreadable);

        Assert.All(refused, static group => Assert.Equal(1000, group.PairingFloorPermille));

        Assert.DoesNotContain(refused.SelectMany(static group => group.Evidence), static candidate => candidate.Lane == LongRestDiscoveryLane.Vector);
    }

    [Fact]
    public void The_spread_cache_keeps_a_spread_for_each_generation_and_width_and_drops_the_oldest_past_its_capacity()
    {
        LongRestSpreadCache cache = new();

        Guid generation = Guid.NewGuid();

        Assert.Equal(0.125, cache.Share(generation, 64, 0.125));

        // The first reading stands: the cache never replaces what an anchor already read.
        Assert.Equal(0.125, cache.Share(generation, 64, 0.5));

        Assert.Equal(0.25, cache.Share(generation, 32, 0.25));

        Assert.Equal(0.125, cache.Share(generation, 64, null));

        Assert.Equal(0.25, cache.Share(generation, 32, null));

        Assert.Null(cache.Share(Guid.NewGuid(), 64, null));

        for (int index = 0; index < 8; index++) _ = cache.Share(Guid.NewGuid(), 64, index);

        Assert.Null(cache.Share(generation, 64, null));

        Assert.Null(cache.Share(generation, 32, null));
    }
}
