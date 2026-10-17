using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The lexical bar is a share of what the anchor scores against itself, so it does not move with the size of the corpus. An absolute score would:
// every term's weight grows with the logarithm of the number of memories, so the same two sentences that were not neighbours in a small corpus
// would be neighbours in a large one, and the amount of paid adjudication would grow with the history for no better evidence.
public sealed class LongRestLexicalCoverageTests
{
    [SkippableTheory]
    [InlineData(10)]
    [InlineData(150)]
    public async Task The_lexical_bar_is_the_same_share_of_the_anchor_whatever_the_size_of_the_corpus(int fillers)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        string[] names = ["anchor", "covers", "half", "weak", .. Enumerable.Range(0, fillers).Select(static index => $"m-filler-{index:D3}")];

        await h.SeedAsync("anchor", "opal quartz agate jasper garnet topaz");

        // Six distinct words in the anchor: a neighbour with all of them and one more, one with half of them, and one with a single word.
        await h.SeedAsync("covers", "opal quartz agate jasper garnet topaz onyx");

        await h.SeedAsync("half", "opal quartz agate sunrise harbour meadow");

        await h.SeedAsync("weak", "opal lantern harvest compass meadow river");

        for (int index = 0; index < fillers; index++)
        {
            await h.SeedAsync(names[4 + index], $"unrelated{index} filler{index} sample{index}");
        }

        // A vector of its own for every memory, so only the lexical lane can name a neighbour.
        for (int index = 0; index < names.Length; index++)
        {
            float[] vector = new float[names.Length];

            vector[index] = 1;

            await h.SetEmbeddingAsync(names[index], vector);
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None));

        string[] lexical = [.. groups.SelectMany(group => group.Evidence).Where(candidate => candidate.Lane == LongRestDiscoveryLane.Lexical).Select(candidate => candidate.Target.MemoryId)];

        Assert.Equal(["covers", "half"], lexical);
    }
}
