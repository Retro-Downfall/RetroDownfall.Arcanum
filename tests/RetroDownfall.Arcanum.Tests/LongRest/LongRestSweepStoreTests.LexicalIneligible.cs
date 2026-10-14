using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    private static float[] OneHot(int width, int index)
    {
        float[] vector = new float[width];

        vector[index] = 1;

        return vector;
    }

    private static string SourceId(int index) => "10000000-0000-0000-0000-" + index.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);

    // A lexical hit is verified only if the lane could still take it, and a hit the generation itself has since consolidated is an ineligible
    // one that outranks the eligible rest: it costs a verification and never a place in the lane. Twelve identical memories are
    // consolidated by the sweep's own exact-duplicate decision after capture (eleven are suppressed); they share both of the anchor's tokens,
    // twice over, so every one of them outscores the one eligible memory that shares a single token. If a consolidated hit could take a place,
    // the eight places of the lexical lane would all be theirs and that memory would be gone.
    [Fact]
    public async Task A_lexical_hit_the_sweep_has_consolidated_outranks_eligible_ones_without_taking_a_place()
    {
        int fold = 0;

        await using Fixture fixture = await Fixture.CreateAsync(false, sourceCount: 44,
            text: index => index <= 12 ? "amethyst gem gem" : index == 13 ? "amethyst gem" : index == 14 ? "amethyst eligible" : $"filler{index} other{index}",
            vector: index => OneHot(64, index), foldVector: _ => OneHot(64, 48 + fold++ % 16));

        Assert.Equal(44L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        Assert.Equal(12, work.Inputs.Length);

        Assert.Single(work.Inputs.Select(static input => input.Snapshot.ContentHash).Distinct(StringComparer.OrdinalIgnoreCase));

        _ = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, null, CancellationToken.None));

        Assert.Equal(11L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId,
            await fixture.H.TargetAsync(SourceId(13)), null, CancellationToken.None));

        string[] lexical = [.. groups.SelectMany(static group => group.Evidence).Where(static candidate => candidate.Lane == LongRestDiscoveryLane.Lexical)
            .Select(static candidate => candidate.Target.MemoryId).Order(StringComparer.Ordinal)];

        // The survivor of the decision and the one eligible memory with a single shared token; nothing the sweep consolidated.
        Assert.Equal(2, lexical.Length);

        Assert.Contains(SourceId(14), lexical);

        Assert.Single(lexical, static memory => string.CompareOrdinal(memory, SourceId(13)) < 0);

        long consolidated = (long)(await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"))!;

        foreach (string memory in lexical.Where(static memory => memory != SourceId(14)))
        {
            Assert.Equal(0L, await fixture.H.ScalarAsync($"SELECT count(*) FROM long_rest_suppressions suppression JOIN long_rest_discovery_leaves leaf ON leaf.VersionId = suppression.SourceVersionId WHERE leaf.MemoryId = '{memory}';"));
        }

        Assert.Equal(11L, consolidated);
    }
}
