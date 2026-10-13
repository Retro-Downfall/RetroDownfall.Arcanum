using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestDiscoveryStoreTests
{
    [SkippableFact]
    public async Task A_vector_neighbor_survives_its_later_eviction_from_the_lexical_lane()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("anchor", "opal");

        await h.SeedAsync("a-vector", "opal " + string.Join(' ', Enumerable.Repeat("filler", 200)));

        for (int index = 0; index < 9; index++)
        {
            string memory = $"b-lexical-{index}";

            await h.SeedAsync(memory, "opal gem");

            float[] vector = new float[64];

            vector[1] = 1;

            await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Embedding = $vector WHERE MemoryId = $memory;",
                ("$vector", EmbeddingBlobCodec.Encode(vector)), ("$memory", memory));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        Assert.Contains(groups.SelectMany(group => group.Evidence), candidate => candidate.Target.MemoryId == "a-vector" && candidate.Lane == LongRestDiscoveryLane.Vector);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lexical_uses_token_matches_and_BM25_with_deterministic_ties(bool accelerated)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        h.Saga.VectorAccelerator.SetAvailable(accelerated);

        await h.SeedAsync("anchor", "opal OR quartz");

        await h.SeedAsync("z-token", "opal opal gem");

        await h.SeedAsync("a-token", "opal opal gem");

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

}
